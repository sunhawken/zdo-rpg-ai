using ZdoRpgAi.Core;
using ZdoRpgAi.Server.Bootstrap;

namespace ZdoRpgAi.Server.Game.Director;

/// <summary>
/// Periodically rolls the dice on starting a spontaneous NPC-to-NPC ambient exchange between two
/// NPCs currently near the player. Purely additive flavor -- see Director.TryStartAmbientDialogueAsync
/// for how it stays safely serialized with real player/NPC activity.
/// </summary>
public class AmbientDialogueScheduler {
    private static readonly ILog Log = Logger.Get<AmbientDialogueScheduler>();

    private readonly Director _director;
    private readonly DirectorHelper _directorHelper;
    private readonly WorldState _worldState;
    private readonly AmbientDialogueSection _config;
    private readonly Random _random = new();

    public AmbientDialogueScheduler(Director director, DirectorHelper directorHelper, WorldState worldState, AmbientDialogueSection config) {
        _director = director;
        _directorHelper = directorHelper;
        _worldState = worldState;
        _config = config;
    }

    public async Task RunAsync(CancellationToken ct) {
        if (!_config.Enabled) {
            Log.Info("Ambient dialogue scheduler disabled");
            return;
        }

        Log.Info("Ambient dialogue scheduler started: every {IntervalSec}s, {Chance:P0} chance",
            _config.CheckIntervalSec, _config.ChancePerCheck);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, _config.CheckIntervalSec)));
        try {
            while (await timer.WaitForNextTickAsync(ct)) {
                await TickAsync();
            }
        }
        catch (OperationCanceledException) {
            // Normal shutdown
        }
    }

    private async Task TickAsync() {
        try {
            var playerId = _worldState.PlayerIds.FirstOrDefault();
            if (playerId == null) {
                Log.Trace("Ambient tick: no known player yet");
                return;
            }

            // Defense in depth: the mod's own activeNpcs table should never include the player
            // (Morrowind's player character is itself an NPC-type object, so it's an easy leak),
            // but never let the player get picked as a radiant-dialogue participant regardless.
            var nearby = (await _directorHelper.QueryObserverIdsAsync(playerId, null))
                .Where(id => !_worldState.IsPlayer(id))
                .ToArray();
            if (nearby.Length < 2) {
                Log.Trace("Ambient tick: only {Count} NPC(s) nearby, need 2+", nearby.Length);
                return;
            }

            if (_random.NextDouble() >= _config.ChancePerCheck) {
                Log.Trace("Ambient tick: dice roll missed");
                return;
            }

            var shuffled = nearby.OrderBy(_ => _random.Next()).ToArray();
            var speaker = shuffled[0];
            var target = shuffled[1];
            Log.Debug("Ambient tick: starting exchange {Speaker} -> {Target}", speaker, target);
            await _director.TryStartAmbientDialogueAsync(speaker, target);
        }
        catch (Exception ex) {
            Log.Warn("Ambient dialogue tick failed: {Error}", ex.Message);
        }
    }
}
