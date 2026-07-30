using System.Text;
using System.Text.Json.Nodes;
using ZdoRpgAi.Core;

namespace ZdoRpgAi.Server.TextToSpeech.OpenAi;

/// <summary>
/// Text-to-speech via any OpenAI-compatible /v1/audio/speech endpoint (OpenAI itself, or an
/// aggregator like NanoGPT that proxies the same API surface). The response body is the raw
/// mp3 bytes directly, not a JSON envelope.
/// </summary>
public class OpenAiTextToSpeech : ITextToSpeech {
    private static readonly ILog Log = Logger.Get<OpenAiTextToSpeech>();

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _baseUrl;
    private readonly string _fallbackVoice;
    private readonly Dictionary<string, string> _voiceByRaceSex;

    public OpenAiTextToSpeech(OpenAiTtsConfig config) {
        ArgumentNullException.ThrowIfNull(config.VoiceMapping.Fallback, "OpenAi.VoiceMapping.Fallback");
        _model = config.Model;
        _baseUrl = config.BaseUrl.TrimEnd('/');
        _fallbackVoice = config.VoiceMapping.Fallback;
        _voiceByRaceSex = config.VoiceMapping.ByRaceSex;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ApiKey}");
    }

    public async Task<ITextToSpeechOutput> GenerateAsync(ITextToSpeechInput input) {
        var voice = ResolveVoice(input.npcRace, input.npcSex);

        var body = new JsonObject {
            ["model"] = _model,
            ["input"] = input.text,
            ["voice"] = voice,
            ["response_format"] = "mp3",
        };

        var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        Log.Debug("Synthesizing {Length} chars with voice {Voice} for NPC {NpcId}", input.text.Length, voice, input.npcId);

        var resp = await _http.PostAsync($"{_baseUrl}/v1/audio/speech", content);

        if (!resp.IsSuccessStatusCode) {
            var error = await resp.Content.ReadAsStringAsync();
            Log.Error("API error {StatusCode}: {Response}", resp.StatusCode, error);
            throw new Exception($"OpenAI TTS API error {resp.StatusCode}: {error}");
        }

        var audio = await resp.Content.ReadAsByteArrayAsync();
        Log.Debug("Received {Size} bytes of audio", audio.Length);
        return new ITextToSpeechOutput { Mp3Bytes = audio };
    }

    private string ResolveVoice(string race, string sex) {
        var key = $"{char.ToLowerInvariant(race[0])}{char.ToLowerInvariant(sex[0])}";

        if (_voiceByRaceSex.TryGetValue(key, out var voice)) {
            return voice;
        }

        Log.Warn("No voice mapping for key '{Key}' (race={Race}, sex={Sex}), using fallback", key, race, sex);
        return _fallbackVoice;
    }
}
