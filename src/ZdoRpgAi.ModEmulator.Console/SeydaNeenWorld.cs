using ZdoRpgAi.Protocol.Messages;

namespace ZdoRpgAi.ModEmulator.Console;

public record NpcDefinition(string ObjectId, string Name, string Race, string Sex, float X, float Y, string Class = "Commoner", string? Faction = null, string? FactionRank = null, bool IsDead = false, float HealthCurrent = 50f, float HealthMax = 50f);

public static class SeydaNeenWorld {
    public const string CellName = "Seyda Neen";
    public const string PlayerId = "player";

    // Mix of no-faction and faction+rank NPCs, including a real Morrowind faction rivalry
    // (Great House vs. Imperial Legion) to exercise the "let faction color the reply" prompt text.
    public static readonly NpcDefinition[] Npcs = [
        new("fargoth", "Fargoth", "Wood Elf", "Male", 10f, 5f, "Thief"),
        new("arrille", "Arrille", "High Elf", "Male", 50f, 30f, "Trader"),
        new("sellus_gravius", "Sellus Gravius", "Imperial", "Male", 5f, 2f, "Spy", "Imperial Legion", "Under-Officer"),
        new("hrisskar_flat-foot", "Hrisskar Flat-Foot", "Nord", "Male", 55f, 32f, "Warrior", "Fighters Guild", "Protector"),
    ];

    public static NpcDefinition? FindNpc(string npcId) =>
        Npcs.FirstOrDefault(n => string.Equals(n.ObjectId, npcId, StringComparison.OrdinalIgnoreCase));

    public static GetNpcInfoResponsePayload ToNpcInfo(NpcDefinition npc) =>
        new(npc.ObjectId, npc.Name, npc.Race, npc.Sex, npc.Class, npc.Faction, npc.FactionRank);

    public static GetPlayerInfoResponsePayload PlayerInfo =>
        new(PlayerId, "Player", "Dark Elf", "Male", "Warrior", "House Hlaalu", "Oathman",
            ["Arrille has offered to buy any moon sugar I bring back from Addamasartus, no questions asked."]);

    public static GetLiveStateResponsePayload ToLiveState(NpcDefinition npc) =>
        new(npc.IsDead, npc.HealthCurrent, npc.HealthMax, CellName);

    public static NearbyCharacterInfo[] GetCharactersWhoHear(string speakerId, float? maxDistance) {
        var speaker = FindNpc(speakerId);
        float sx = speaker?.X ?? 0f;
        float sy = speaker?.Y ?? 0f;

        var result = new List<NearbyCharacterInfo>();
        foreach (var npc in Npcs) {
            if (string.Equals(npc.ObjectId, speakerId, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var dist = MathF.Sqrt((npc.X - sx) * (npc.X - sx) + (npc.Y - sy) * (npc.Y - sy));
            if (maxDistance.HasValue && dist > maxDistance.Value) {
                continue;
            }

            result.Add(new NearbyCharacterInfo(npc.ObjectId, dist));
        }

        return result.ToArray();
    }
}
