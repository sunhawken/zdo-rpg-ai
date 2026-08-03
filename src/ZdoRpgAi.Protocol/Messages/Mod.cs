namespace ZdoRpgAi.Protocol.Messages;

// Mod → Server

public enum ModToServerMessageType {
    PlayerAdded,
    TargetChanged,
    CellChange,
    GameSaveLoad,
    GetCharactersWhoHearResponse,
    // Sent by the per-NPC local combat script (scripts/zdorpgai/npc_combat.lua) -- NpcCombatStarted
    // on the rising edge of entering an AiCombat package against TargetId (player or another NPC),
    // NpcCombatTick periodically thereafter while still fighting the same target, each driving one
    // combat-flavored bark from NpcId directed at TargetId (see Director.TryStartCombatBarkAsync).
    NpcCombatStarted,
    NpcCombatTick,
}

public record PlayerAddedPayload(string PlayerId);
public record TargetChangedPayload(string PlayerId, string? NpcId);
public record CellChangePayload(string PlayerId, string CellName);
public record GameSaveLoadPayload();
public record NpcCombatEventPayload(string NpcId, string TargetId);
public record NearbyCharacterInfo(string CharacterId, float DistanceMeters);
public record GetCharactersWhoHearResponsePayload(NearbyCharacterInfo[] Characters);
