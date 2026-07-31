namespace ZdoRpgAi.Protocol.Messages;

// Client → Mod

public enum ClientToModMessageType {
    SayMp3File,
}

public record SayMp3FilePayload(string NpcId, string Mp3Name, string Text, double? DurationSec = null);

// Mod → Client

public record StartSessionAckPayload(string SessionId);

// A VR controller button bound in-game to the mod's push-to-talk action (see
// zdorpgai/scripts/zdorpgai/player.lua). Purely a local trigger for VoiceCaptureService --
// unlike ModToServerMessageType members, these never get forwarded to the server.
public enum ModToClientMessageType {
    VrPttPressed,
    VrPttReleased,
    // Left thumb-rest touch (mirrors the right thumb-rest PTT trigger): tap to flip hot mic
    // on/off, the VR equivalent of the keyboard hot-mic toggle hotkey.
    VrHotMicTogglePressed,
    // Player submitted the in-game chat box (player.lua TextEdit widget, opened with the T key).
    // Unlike the other members here, this DOES get turned into a real server message -- see
    // ClientApplication.HandleModMessage, which repackages it as PlayerSpeaksText.
    ChatBoxTextSubmitted,
}

public record ChatBoxTextSubmittedPayload(string Text, string? TargetCharacterId);

// Client → Both (Mod + Server)

public enum ClientToBothMessageType {
    PlayerStartSpeak,
    PlayerStopSpeak,
}

public record PlayerStartSpeakPayload(string PlayerId, string? TargetCharacterId, string GameTime);
public record PlayerStopSpeakPayload(string PlayerId, bool Cancel = false);

// Client → Server

public enum ClientToServerMessageType {
    PlayerSpeaksText,
    PlayerSpeaksAudio,
}

public record PlayerSpeaksTextPayload(string PlayerId, string Text, string? TargetCharacterId, string GameTime);
public record PlayerSpeaksAudioPayload(string PlayerId);
