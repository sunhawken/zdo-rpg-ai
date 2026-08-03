using ZdoRpgAi.Core;
using ZdoRpgAi.Protocol.Channel;
using ZdoRpgAi.Protocol.Messages;
using ZdoRpgAi.Protocol.Rpc;
using ZdoRpgAi.Server.Game.Director;

namespace ZdoRpgAi.Server.Game.Story;

public class StoryComposer {
    private static readonly ILog Log = Logger.Get<StoryComposer>();

    private readonly Story _story;
    private readonly DirectorHelper _directorHelper;
    private readonly WorldState _worldState;
    private readonly Director.Director _director;

    public StoryComposer(Story story, DirectorHelper directorHelper, WorldState worldState, Director.Director director, IRpcChannel rpc) {
        _story = story;
        _directorHelper = directorHelper;
        _worldState = worldState;
        _director = director;
        rpc.MessageReceived += OnMessageReceived;
    }

    public void OnPlayerSpeak(string playerId, string? targetCharacterId, string gameTime, string text) {
        Log.Debug("OnPlayerSpeak");
        _ = OnPlayerSpeakAsync(playerId, targetCharacterId, gameTime, text);
    }

    private async Task OnPlayerSpeakAsync(string playerId, string? targetCharacterId, string gameTime, string text) {
        try {
            Log.Trace("Querying observers for player {PlayerId}", playerId);
            var observerIds = await _directorHelper.QueryObserverIdsAsync(playerId, targetCharacterId != null ? [targetCharacterId] : null);
            Log.Trace("Got {Count} observers, registering event", observerIds.Length);

            var evt = StoryEvent.Create(new StoryEvent.PlayerSpeak {
                PlayerCharacterId = playerId,
                TargetCharacterId = targetCharacterId,
                GameTime = gameTime,
                Text = text,
            });
            _story.RegisterEvent(evt, observerIds);
        }
        catch (Exception ex) {
            Log.Error("OnPlayerSpeakAsync failed: {Error}", ex.Message);
        }
    }

    private void OnMessageReceived(Message msg) {
        switch (msg.Type) {
            case nameof(ClientToServerMessageType.PlayerSpeaksText): {
                    var payload = msg.Json?.DeserializeSafe(PayloadJsonContext.Default.PlayerSpeaksTextPayload);
                    if (payload == null) {
                        return;
                    }

                    OnPlayerSpeak(payload.PlayerId, payload.TargetCharacterId, payload.GameTime, payload.Text);
                    break;
                }
            case nameof(ModToServerMessageType.CellChange): {
                    var payload = msg.Json?.DeserializeSafe(PayloadJsonContext.Default.CellChangePayload);
                    if (payload == null) {
                        return;
                    }

                    _worldState.SetCurrentCell(payload.CellName);
                    break;
                }
            case nameof(ModToServerMessageType.PlayerAdded): {
                    var payload = msg.Json?.DeserializeSafe(PayloadJsonContext.Default.PlayerAddedPayload);
                    if (payload == null) {
                        return;
                    }

                    _worldState.AddPlayerId(payload.PlayerId);
                    break;
                }
            case nameof(ModToServerMessageType.NpcCombatStarted):
            case nameof(ModToServerMessageType.NpcCombatTick): {
                    var payload = msg.Json?.DeserializeSafe(PayloadJsonContext.Default.NpcCombatEventPayload);
                    if (payload == null) {
                        return;
                    }

                    _ = _director.TryStartCombatBarkAsync(payload.NpcId, payload.TargetId);
                    break;
                }
        }
    }
}
