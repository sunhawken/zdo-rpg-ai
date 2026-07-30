using ZdoRpgAi.Core;
using ZdoRpgAi.Protocol.Channel;
using ZdoRpgAi.Protocol.Messages;
using ZdoRpgAi.Protocol.Rpc;
using ZdoRpgAi.Server.SpeechToText;

namespace ZdoRpgAi.Server.Game;

public class PlayerMessageHandler {
    private static readonly ILog Log = Logger.Get<PlayerMessageHandler>();

    private readonly ISpeechToText _stt;
    private readonly IRpcChannel _rpc;
    private SpeakingSession? _activeSession;
    // FIFO, not a single field: if the player starts a new utterance before the previous one's
    // STT call has returned (now expected, since Director no longer blocks the player on a prior
    // NPC's response), more than one transcription can be in flight at once. Final results should
    // arrive in the order they were requested.
    private readonly Queue<SpeakingSession> _finishingSessions = new();

    public event Action<string, string?, string, string>? PlayerSpoke;

    public PlayerMessageHandler(ISpeechToText stt, IRpcChannel rpc) {
        _stt = stt;
        _rpc = rpc;
        rpc.MessageReceived += OnMessageReceived;
        _stt.InterimResultReceived += OnInterimResultReceived;
        _stt.FinalResultReceived += OnFinalResultReceived;
    }

    private void OnMessageReceived(Message msg) {
        switch (msg.Type) {
            case nameof(ClientToServerMessageType.PlayerSpeaksText):
                HandlePlayerSpeaksText(msg);
                break;
            case nameof(ClientToBothMessageType.PlayerStartSpeak):
                HandlePlayerStartSpeak(msg);
                break;
            case nameof(ClientToServerMessageType.PlayerSpeaksAudio):
                HandlePlayerSpeaksAudio(msg);
                break;
            case nameof(ClientToBothMessageType.PlayerStopSpeak):
                HandlePlayerStopSpeak(msg);
                break;
        }
    }

    private void HandlePlayerStartSpeak(Message msg) {
        var payload = msg.Json?.DeserializeSafe(PayloadJsonContext.Default.PlayerStartSpeakPayload);
        if (payload == null) {
            return;
        }

        if (_activeSession != null) {
            Log.Warn("Player {PlayerId} started speaking while a session is already active, ignoring",
                payload.PlayerId);
            return;
        }

        Log.Info("Player {PlayerId} started speaking", payload.PlayerId);
        _activeSession = new SpeakingSession(payload.PlayerId, payload.TargetCharacterId, payload.GameTime);
        _stt.Start();
    }

    private void HandlePlayerSpeaksAudio(Message msg) {
        if (_activeSession == null) {
            Log.Warn("Received audio without an active speaking session");
            return;
        }
        if (msg.Binary == null) {
            Log.Warn("Received PlayerSpeaksAudio without binary data");
            return;
        }
        _stt.FeedAudio(msg.Binary);
    }

    private void HandlePlayerStopSpeak(Message msg) {
        var session = _activeSession;
        if (session == null) {
            Log.Warn("Received PlayerStopSpeak without an active speaking session");
            return;
        }

        var payload = msg.Json?.DeserializeSafe(PayloadJsonContext.Default.PlayerStopSpeakPayload);
        if (payload == null) {
            return;
        }

        _activeSession = null;

        if (payload.Cancel) {
            Log.Info("Player {PlayerId} cancelled speaking", payload.PlayerId);
            _stt.Cancel();
        }
        else {
            Log.Info("Player {PlayerId} stopped speaking", payload.PlayerId);
            _finishingSessions.Enqueue(session);
            _stt.Finish();
        }
    }

    private void OnInterimResultReceived(string text) {
        var session = _activeSession;
        if (session == null) {
            Log.Warn("Received interim STT result but no active session");
            return;
        }

        Log.Debug("Interim recognition for {PlayerId}: {Text}", session.PlayerId, text);
        _rpc.Publish(
            nameof(ServerToModMessageType.SpeechRecognitionInProgress),
            JsonExtensions.SerializeToObject(
                new SpeechRecognitionInProgressPayload(session.PlayerId, text),
                PayloadJsonContext.Default.SpeechRecognitionInProgressPayload));
    }

    private void OnFinalResultReceived(string text) {
        SpeakingSession? session;
        if (_finishingSessions.Count > 0) {
            session = _finishingSessions.Dequeue();
        }
        else {
            // Defensive fallback for a final result arriving without a matching StopSpeak --
            // _activeSession only belongs here (and only gets cleared here) in that case, since
            // the normal flow already clears it synchronously in HandlePlayerStopSpeak and a new
            // utterance may legitimately be active by the time this fires.
            session = _activeSession;
            _activeSession = null;
        }

        if (session == null) {
            Log.Warn("Received final STT result but no active session");
            return;
        }

        Log.Info("Final recognition for {PlayerId}: {Text}", session.PlayerId, text);
        _rpc.Publish(
            nameof(ServerToModMessageType.SpeechRecognitionComplete),
            JsonExtensions.SerializeToObject(
                new SpeechRecognitionCompletePayload(session.PlayerId, text),
                PayloadJsonContext.Default.SpeechRecognitionCompletePayload));

        PlayerSpoke?.Invoke(session.PlayerId, session.TargetCharacterId, session.GameTime, text);
    }

    private void HandlePlayerSpeaksText(Message msg) {
        var payload = msg.Json?.DeserializeSafe(PayloadJsonContext.Default.PlayerSpeaksTextPayload);
        if (payload == null) {
            return;
        }

        Log.Info("Player {PlayerId} speaks: {Text}", payload.PlayerId, payload.Text);

        PlayerSpoke?.Invoke(payload.PlayerId, payload.TargetCharacterId, payload.GameTime, payload.Text);
    }

    private record SpeakingSession(string PlayerId, string? TargetCharacterId, string GameTime);
}
