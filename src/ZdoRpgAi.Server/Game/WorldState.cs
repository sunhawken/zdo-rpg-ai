namespace ZdoRpgAi.Server.Game;

/// <summary>
/// Tiny in-memory tracker for world facts pushed from the mod that arrive as fire-and-forget
/// events (nothing round-trips to ask "what cell am I in right now") rather than being fetched
/// on demand. Deliberately not persisted -- rebuilt from the next CellChange/PlayerAdded after
/// any restart.
/// </summary>
public class WorldState {
    private readonly HashSet<string> _playerIds = new();

    public string? CurrentCellName { get; private set; }

    // Single-player game, but tracked as a set (rather than one "the" player id) since nothing
    // stops a save from re-adding the player under a different recordId across a reload.
    public IReadOnlyCollection<string> PlayerIds => _playerIds;

    public void SetCurrentCell(string cellName) {
        CurrentCellName = cellName;
    }

    public void AddPlayerId(string playerId) {
        _playerIds.Add(playerId);
    }

    public bool IsPlayer(string characterId) => _playerIds.Contains(characterId);
}
