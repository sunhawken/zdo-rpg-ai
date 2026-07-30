using System.Text.Json.Nodes;
using ZdoRpgAi.Core;

namespace ZdoRpgAi.Server.SpeechToText.OpenAi;

/// <summary>
/// Speech-to-text via any OpenAI-Whisper-compatible /v1/audio/transcriptions endpoint
/// (OpenAI itself, or an aggregator like NanoGPT that proxies the same API surface).
/// Unlike Deepgram this is a single batch request per utterance, not a streaming session:
/// audio is buffered for the whole push-to-talk hold and transcribed once on Finish().
/// </summary>
public class OpenAiSpeechToText : ISpeechToText {
    private static readonly ILog Log = Logger.Get<OpenAiSpeechToText>();

    private readonly HttpClient _http = new();
    private readonly string _model;
    private readonly string _baseUrl;
    private readonly int _sampleRate;
    private readonly string? _language;

    private readonly object _lock = new();
    private List<byte[]>? _buffers;
    private CancellationTokenSource? _cts;

    public event Action<string>? InterimResultReceived { add { } remove { } }
    public event Action<string>? FinalResultReceived;

    public OpenAiSpeechToText(OpenAiSttConfig config) {
        _model = config.Model;
        _baseUrl = config.BaseUrl.TrimEnd('/');
        _sampleRate = config.SampleRate;
        _language = config.Language;
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ApiKey}");
    }

    public void Start() {
        lock (_lock) {
            _buffers = new List<byte[]>();
            _cts = new CancellationTokenSource();
        }
    }

    public void FeedAudio(ReadOnlyMemory<byte> buffer) {
        lock (_lock) {
            if (_buffers == null) {
                Log.Warn("FeedAudio called without active session");
                return;
            }
            _buffers.Add(buffer.ToArray());
        }
    }

    public void Finish() {
        List<byte[]>? buffers;
        CancellationTokenSource? cts;
        lock (_lock) {
            buffers = _buffers;
            cts = _cts;
            _buffers = null;
            _cts = null;
        }

        if (buffers == null || cts == null) {
            Log.Warn("Finish called without active session");
            return;
        }

        var totalBytes = buffers.Sum(b => b.Length);
        if (totalBytes == 0) {
            Log.Warn("Finish called with no buffered audio");
            return;
        }

        _ = TranscribeAsync(buffers, totalBytes, cts.Token);
    }

    public void Cancel() {
        lock (_lock) {
            _cts?.Cancel();
            _cts = null;
            _buffers = null;
        }
    }

    public void Dispose() {
        Cancel();
        _http.Dispose();
    }

    private async Task TranscribeAsync(List<byte[]> buffers, int totalBytes, CancellationToken ct) {
        try {
            var wav = BuildWav(buffers, totalBytes, _sampleRate);

            // HttpClient's MultipartFormDataContent.Add(content, name[, fileName]) shorthand
            // writes UNQUOTED Content-Disposition parameters (name=file instead of name="file"),
            // plus an extra filename*=utf-8''... parameter for file uploads. That's legal enough
            // for lenient parsers, but this server's (Vercel/Node-based) multipart parser
            // silently fails the whole request over it -- for every field, not just the file --
            // and reports it back as a generic Internal Server Error with zero detail. Also
            // strips the (separately, also real) quoting HttpClient adds around the boundary
            // parameter itself. Root-caused by replaying a dumped failing WAV through curl
            // (succeeds) vs this code (500s) and diffing the raw request bytes.
            using var form = new MultipartFormDataContent();

            var audioContent = new ByteArrayContent(wav);
            audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            audioContent.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data") {
                Name = "\"file\"",
                FileName = "\"audio.wav\"",
            };
            form.Add(audioContent);

            AddField(form, "model", _model);
            if (!string.IsNullOrEmpty(_language)) {
                AddField(form, "language", _language);
            }

            var boundaryParam = form.Headers.ContentType?.Parameters.FirstOrDefault(p => p.Name == "boundary");
            if (boundaryParam != null) {
                boundaryParam.Value = boundaryParam.Value?.Trim('"');
            }

            Log.Debug("Transcribing {Bytes} bytes of audio ({SampleRate}Hz)", totalBytes, _sampleRate);

            var resp = await _http.PostAsync($"{_baseUrl}/v1/audio/transcriptions", form, ct);
            var respJson = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode) {
                Log.Error("API error {StatusCode}: {Response}", resp.StatusCode, respJson);
                try {
                    var dumpPath = Path.Combine(Path.GetTempPath(), $"zdorpgai_failed_stt_{DateTime.Now:yyyyMMdd_HHmmss_fff}.wav");
                    await File.WriteAllBytesAsync(dumpPath, wav, CancellationToken.None);
                    Log.Error("Dumped failing audio to {Path} ({Bytes} bytes)", dumpPath, wav.Length);
                }
                catch (Exception dumpEx) {
                    Log.Warn("Failed to dump failing audio: {Error}", dumpEx.Message);
                }
                return;
            }

            var text = JsonNode.Parse(respJson)?["text"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(text)) {
                Log.Warn("Transcription returned empty text. Raw response: {Response}", respJson);
                return;
            }

            Log.Debug("Transcribed: '{Text}'", text);
            FinalResultReceived?.Invoke(text.Trim());
        }
        catch (OperationCanceledException) {
            Log.Debug("Transcription cancelled");
        }
        catch (Exception ex) {
            Log.Error("Unexpected error transcribing audio: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// Adds a plain text multipart field with a properly quoted Content-Disposition name and no
    /// Content-Type -- see the comment in TranscribeAsync for why the normal
    /// MultipartFormDataContent.Add(content, name) shorthand doesn't work against this server.
    /// </summary>
    private static void AddField(MultipartFormDataContent form, string name, string value) {
        var content = new StringContent(value);
        content.Headers.ContentType = null;
        content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data") {
            Name = $"\"{name}\"",
        };
        form.Add(content);
    }

    /// <summary>Wraps raw 16-bit PCM mono samples in a minimal 44-byte RIFF/WAVE header.</summary>
    private static byte[] BuildWav(List<byte[]> buffers, int dataLength, int sampleRate) {
        const int bitsPerSample = 16;
        const int channels = 1;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = channels * bitsPerSample / 8;

        var wav = new byte[44 + dataLength];
        void WriteAscii(int offset, string s) {
            for (var i = 0; i < s.Length; i++) wav[offset + i] = (byte)s[i];
        }
        void WriteUInt32(int offset, uint v) {
            wav[offset] = (byte)v; wav[offset + 1] = (byte)(v >> 8);
            wav[offset + 2] = (byte)(v >> 16); wav[offset + 3] = (byte)(v >> 24);
        }
        void WriteUInt16(int offset, ushort v) {
            wav[offset] = (byte)v; wav[offset + 1] = (byte)(v >> 8);
        }

        WriteAscii(0, "RIFF");
        WriteUInt32(4, (uint)(36 + dataLength));
        WriteAscii(8, "WAVE");
        WriteAscii(12, "fmt ");
        WriteUInt32(16, 16);
        WriteUInt16(20, 1); // PCM
        WriteUInt16(22, channels);
        WriteUInt32(24, (uint)sampleRate);
        WriteUInt32(28, (uint)byteRate);
        WriteUInt16(32, (ushort)blockAlign);
        WriteUInt16(34, bitsPerSample);
        WriteAscii(36, "data");
        WriteUInt32(40, (uint)dataLength);

        var pos = 44;
        foreach (var buf in buffers) {
            Buffer.BlockCopy(buf, 0, wav, pos, buf.Length);
            pos += buf.Length;
        }

        return wav;
    }
}
