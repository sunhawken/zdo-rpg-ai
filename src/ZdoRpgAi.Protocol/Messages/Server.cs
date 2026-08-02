namespace ZdoRpgAi.Protocol.Messages;

// Server → Client

public enum ServerToClientMessageType {
    NpcSpeaksMp3,
}

public record NpcSpeaksMp3Payload(string NpcId, string Text, double DurationSec);

// Server → Mod

public enum ServerToModMessageType {
    SpeechRecognitionInProgress,
    SpeechRecognitionComplete,
    GetCharactersWhoHear,
    GetNpcInfo,
    GetPlayerInfo,
    GetLiveState,
    SpawnOnGroundInFrontOfCharacter,
    PlaySound3dOnCharacter,
    NpcStartFollowCharacter,
    NpcStopFollowCharacter,
    NpcAttack,
    NpcStopAttack,
    ShowMessageBox,
}

public record SpeechRecognitionInProgressPayload(string PlayerId, string Text);
public record SpeechRecognitionCompletePayload(string PlayerId, string Text);
public record GetCharactersWhoHearRequestPayload(string CharacterId, float? MaxDistanceMeters = null);
public record GetNpcInfoRequestPayload(string NpcId);
public record GetNpcInfoResponsePayload(string ObjectId, string Name, string Race, string Sex, string? Class = null, string? Faction = null, string? FactionRank = null);
public record GetPlayerInfoRequestPayload(string PlayerId);
// ActiveQuests: the player's most recent journal text entry for each started-but-not-finished
// quest (e.g. "Aryon has asked me to retrieve the Ring of Khajiit..."), so NPCs talking to the
// player can be aware of what they're currently up to. Fetched fresh each time, same as the rest
// of GetPlayerInfo -- quests are dynamic, never cached.
public record GetPlayerInfoResponsePayload(string ObjectId, string Name, string Race, string Sex, string? Class = null, string? Faction = null, string? FactionRank = null, string[]? ActiveQuests = null);

// Live, uncached dynamic state (health/dead/location) for a character -- deliberately kept
// separate from GetNpcInfo/GetPlayerInfo, which cache their (static) results indefinitely.
public record GetLiveStateRequestPayload(string CharacterId);
public record GetLiveStateResponsePayload(bool IsDead, float HealthCurrent, float HealthMax, string? CellName);
public record SpawnOnGroundInFrontOfCharacterPayload(string NpcId, string ItemId, int Count = 1);
public record PlaySound3dOnCharacterPayload(string NpcId, string Sound);
public record NpcStartFollowCharacterPayload(string NpcId, string TargetCharacterId);
public record NpcStopFollowCharacterPayload(string NpcId);
public record NpcAttackPayload(string NpcId, string TargetCharacterId);
public record NpcStopAttackPayload(string NpcId);
public record ShowMessageBoxPayload(string Message);
