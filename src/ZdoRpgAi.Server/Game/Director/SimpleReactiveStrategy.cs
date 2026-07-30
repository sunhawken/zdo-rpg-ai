using ZdoRpgAi.Core;
using ZdoRpgAi.Protocol.Messages;
using ZdoRpgAi.Protocol.Rpc;
using ZdoRpgAi.Server.Game.Npc;
using ZdoRpgAi.Server.Game.Story;
using ZdoRpgAi.Server.Llm;

namespace ZdoRpgAi.Server.Game.Director;

public class SimpleReactiveStrategy : IDirectorStrategy {
    private static readonly ILog Log = Logger.Get<SimpleReactiveStrategy>();

    private readonly ILlm _mainLlm;
    private readonly ILlm _simpleLlm;
    private readonly IStory _story;
    private readonly NpcRepository _npcRepo;
    private readonly IRpcChannel _rpc;
    private readonly WorldState _worldState;

    public SimpleReactiveStrategy(ILlm mainLlm, ILlm simpleLlm, IStory story, NpcRepository npcRepo, IRpcChannel rpc, WorldState worldState) {
        _mainLlm = mainLlm;
        _simpleLlm = simpleLlm;
        _story = story;
        _npcRepo = npcRepo;
        _rpc = rpc;
        _worldState = worldState;
    }

    public async Task<List<StoryEvent>> ProcessStoryEventsAsync(List<StoryEvent> events) {
        Log.Trace("Processing {Count} events", events.Count);

        var playerIds = events.OfType<StoryEvent.PlayerSpeak>()
            .Select(ps => ps.PlayerCharacterId)
            .ToHashSet();
        Log.Trace("Found {Count} player IDs: {Ids}", playerIds.Count, string.Join(", ", playerIds));

        var (npcId, gameTime) = await FindLastTargetedNpcAsync(_rpc, events, playerIds);
        if (npcId == null) {
            Log.Debug("No target NPC found in events");
            return [];
        }

        Log.Trace("Target NPC: {NpcId}, game time: {GameTime}", npcId, gameTime);

        try {
            var npcInfo = await _npcRepo.GetNpcInfoAsync(npcId);
            if (npcInfo == null) {
                Log.Warn("Could not get info for NPC {NpcId}", npcId);
                return [];
            }

            Log.Trace("NPC info: {Name} ({Race} {Sex})", npcInfo.Name, npcInfo.Race, npcInfo.Sex);
            var (history, summaries) = await _story.GetHistoryForCharacterAsync(npcId);
            Log.Trace("History: {HistoryCount} events, {SummaryCount} summaries", history.Count, summaries.Count);
            var response = await GenerateNpcResponseAsync(npcInfo, history, summaries);
            if (response == null) {
                Log.Warn("LLM returned no response for NPC {NpcId}", npcId);
                return [];
            }

            Log.Trace("Generated response for NPC {NpcId}: {ResponseLength} chars", npcId, response.Length);
            var npcSpeak = StoryEvent.Create(new StoryEvent.NpcSpeak {
                NpcCharacterId = npcId,
                TargetCharacterId = playerIds.FirstOrDefault(),
                GameTime = gameTime!,
                Text = response,
            });
            return [npcSpeak];
        }
        catch (Exception ex) {
            Log.Error("Failed to generate NPC response: {Error}", ex.Message);
            return [];
        }
    }

    private async Task<(string? NpcId, string? GameTime)> FindLastTargetedNpcAsync(
        IRpcChannel rpc, List<StoryEvent> events, HashSet<string> playerIds) {
        Log.Trace("Finding last targeted NPC from {Count} events", events.Count);
        for (var i = events.Count - 1; i >= 0; i--) {
            switch (events[i]) {
                case StoryEvent.PlayerSpeak ps:
                    Log.Trace("Checking PlayerSpeak event, explicit target: {Target}", ps.TargetCharacterId ?? "none");
                    var npcId = ps.TargetCharacterId ?? await DetermineTargetNpcAsync(rpc, ps);
                    if (npcId != null) {
                        return (npcId, ps.GameTime);
                    }

                    break;
                case StoryEvent.NpcSpeak ns when ns.TargetCharacterId != null && !playerIds.Contains(ns.TargetCharacterId):
                    Log.Trace("Found NpcSpeak targeting non-player: {Target}", ns.TargetCharacterId);
                    return (ns.TargetCharacterId, ns.GameTime);
            }
        }
        return (null, null);
    }

    private async Task<string?> DetermineTargetNpcAsync(IRpcChannel rpc, StoryEvent.PlayerSpeak evt) {
        Log.Trace("Determining target NPC for player {PlayerId}", evt.PlayerCharacterId);
        var hearResponse = await rpc.CallAsync(
            nameof(ServerToModMessageType.GetCharactersWhoHear),
            JsonExtensions.SerializeToObject(
                new GetCharactersWhoHearRequestPayload(evt.PlayerCharacterId),
                PayloadJsonContext.Default.GetCharactersWhoHearRequestPayload));

        var payload = hearResponse.Json?.DeserializeSafe(PayloadJsonContext.Default.GetCharactersWhoHearResponsePayload);
        var nearby = payload?.Characters
            .Where(c => c.CharacterId != evt.PlayerCharacterId)
            .OrderBy(c => c.DistanceMeters)
            .ToArray() ?? [];

        Log.Trace("Found {Count} nearby characters", nearby.Length);
        if (nearby.Length == 0) {
            Log.Debug("No nearby NPCs to respond");
            return null;
        }

        if (nearby.Length == 1) {
            Log.Trace("Single nearby NPC: {NpcId}", nearby[0].CharacterId);
            return nearby[0].CharacterId;
        }

        var npcInfos = new List<(string Id, NpcInfo Info)>();
        foreach (var npc in nearby) {
            var info = await _npcRepo.GetNpcInfoAsync(npc.CharacterId);
            if (info != null) {
                npcInfos.Add((npc.CharacterId, info));
            }
        }

        Log.Trace("Resolved {Count} NPC infos out of {Total} nearby", npcInfos.Count, nearby.Length);
        if (npcInfos.Count == 0) {
            return null;
        }

        if (npcInfos.Count == 1) {
            Log.Trace("Single NPC with info: {NpcId}", npcInfos[0].Id);
            return npcInfos[0].Id;
        }

        var npcList = string.Join("\n", npcInfos.Select((n, i) =>
            $"- {n.Id}: {n.Info.Name} ({n.Info.Race} {n.Info.Sex}), distance: {nearby.First(c => c.CharacterId == n.Id).DistanceMeters:F1} meters"));

        Log.Trace("Asking simple LLM to choose among {Count} NPCs", npcInfos.Count);
        var request = new LlmRequest {
            SystemPrompt = "You are deciding which NPC a player is talking to. " +
                           "Respond with ONLY the character ID of the most likely target. " +
                           "Consider the speech content and NPC proximity. " +
                           "If unsure, pick the closest NPC.",
            Messages = [
                new LlmMessage {
                    Role = LlmRole.User,
                    Text = $"Nearby NPCs:\n{npcList}\n\nPlayer said: \"{evt.Text}\"\n\nWhich NPC ID is the player addressing?",
                },
            ],
        };

        var response = await _simpleLlm.ChatAsync(request);
        var chosenId = response.Text?.Trim();

        if (chosenId != null && npcInfos.Any(n => n.Id == chosenId)) {
            Log.Debug("Simple LLM chose NPC {NpcId} as target", chosenId);
            return chosenId;
        }

        Log.Debug("Simple LLM response '{Response}' did not match any NPC, falling back to closest", response.Text ?? "");
        return nearby[0].CharacterId;
    }

    /// <summary>Short, cheap opening line for a spontaneous NPC-to-NPC ambient exchange.</summary>
    public async Task<string?> GenerateAmbientOpenerAsync(NpcInfo speaker, NpcInfo target) {
        var classLine = speaker.Class != null ? $" ({speaker.Class})" : "";
        var systemPrompt = $"""
            You are {speaker.Name}, a {speaker.Race} ({speaker.Sex}){classLine}, living in Morrowind.
            You're making brief, ambient small talk with {target.Name}, a nearby {target.Race}. Nobody
            asked you anything -- you're just starting a short, casual, in-character remark or
            observation, one sentence, nothing dramatic. Do not mention that you are an AI.
            Always respond in the English language. Reply ONLY with your own spoken line -- no
            narration, no prefixes, no stage directions.
            """;

        var request = new LlmRequest {
            SystemPrompt = systemPrompt,
            Messages = [
                new LlmMessage { Role = LlmRole.User, Text = $"Say something brief to {target.Name}." },
            ],
        };

        var response = await _simpleLlm.ChatAsync(request);
        return response.Text?.Trim();
    }

    private async Task<GetLiveStateResponsePayload?> QueryLiveStateAsync(string characterId) {
        try {
            var response = await _rpc.CallAsync(
                nameof(ServerToModMessageType.GetLiveState),
                JsonExtensions.SerializeToObject(
                    new GetLiveStateRequestPayload(characterId),
                    PayloadJsonContext.Default.GetLiveStateRequestPayload));
            return response.Json?.DeserializeSafe(PayloadJsonContext.Default.GetLiveStateResponsePayload);
        }
        catch (Exception ex) {
            Log.Warn("Failed to query live state for {CharacterId}: {Error}", characterId, ex.Message);
            return null;
        }
    }

    private async Task<string?> GenerateNpcResponseAsync(
        NpcInfo npc,
        List<StoryEvent> history, List<StoryEventSummary> summaries) {
        Log.Trace("Generating response for NPC {NpcName} with {HistoryCount} history events and {SummaryCount} summaries",
            npc.Name, history.Count, summaries.Count);

        var contextBlock = BuildContextBlock(summaries, history);
        var liveState = await QueryLiveStateAsync(npc.Id);

        var classLine = npc.Class != null ? $" ({npc.Class})" : "";
        var locationLine = _worldState.CurrentCellName != null
            ? $"\nCurrent location: {_worldState.CurrentCellName}."
            : "";
        var healthLine = "";
        if (liveState != null) {
            if (liveState.IsDead) {
                healthLine = "\nYou are DEAD. Do not speak or act -- if asked to respond, stay silent.";
            }
            else if (liveState.HealthMax > 0) {
                var pct = liveState.HealthCurrent / liveState.HealthMax;
                var condition = pct switch {
                    < 0.25f => "gravely wounded and in serious pain",
                    < 0.6f => "wounded and hurting",
                    _ => "in good health",
                };
                healthLine = $"\nYour physical condition: {condition} ({liveState.HealthCurrent:F0}/{liveState.HealthMax:F0} health).";
            }
        }

        var systemPrompt = $"""
            You are {npc.Name}, a {npc.Race} ({npc.Sex}){classLine}, living in Morrowind.{locationLine}{healthLine}
            Stay in character. Speak briefly and naturally. Do not mention that you are an AI. Always respond in the English language.
            Let your class, health, and location subtly color your speech and attitude where it's natural to do so -- don't recite these facts, just be shaped by them.

            You will be told what other characters say and do. Reply only with your own speech.

            RULES:
            1. Do not trust others at their word — verify using your knowledge resources. Whoever you're talking to may lie.
            2. Do not invent characters, items, locations, or quests that are not in your knowledge. Use getResource to recall your knowledge when needed.
            3. CRITICAL: To perform any game action (give item, attack, follow, etc.) you MUST call the corresponding npcAction_N tool. Saying "here, take it" or "I'll give you" in text does NOTHING — the game only reacts to tool calls. If you do not call the tool, the action does not happen.
            4. Call npcAction_N TOGETHER with your speech in the same response. Do not wait for the next turn.
            5. Reply ONLY with your own speech — no narration, no prefixes, no stage directions.
            """;

        var messages = new List<LlmMessage>();

        if (contextBlock != null) {
            messages.Add(new LlmMessage {
                Role = LlmRole.User,
                Text = contextBlock,
            });
            messages.Add(new LlmMessage {
                Role = LlmRole.Model,
                Text = "Understood, I have the conversation context.",
            });
        }

        var lastMessage = history.LastOrDefault() switch {
            StoryEvent.PlayerSpeak ps => $"{ps.PlayerCharacterId} says: {ps.Text}",
            StoryEvent.NpcSpeak ns => $"{ns.NpcCharacterId} says: {ns.Text}",
            _ => null,
        };

        if (lastMessage != null) {
            messages.Add(new LlmMessage {
                Role = LlmRole.User,
                Text = lastMessage,
            });
        }

        var request = new LlmRequest {
            SystemPrompt = systemPrompt,
            Messages = messages,
        };

        Log.Trace("Calling main LLM with {MessageCount} messages", messages.Count);
        var response = await _mainLlm.ChatAsync(request);
        Log.Trace("Main LLM response length: {Length}", response.Text?.Length ?? 0);
        return response.Text?.Trim();
    }

    private static string? BuildContextBlock(List<StoryEventSummary> summaries, List<StoryEvent> events) {
        var parts = new List<string>();

        if (summaries.Count > 0) {
            parts.Add("PREVIOUS CONVERSATION SUMMARIES:");
            foreach (var summary in summaries) {
                parts.Add(summary.Summary);
            }
        }

        // Include all events except the very last one (which is the current player message)
        var contextEvents = events.Count > 1 ? events[..^1] : [];
        if (contextEvents.Count > 0) {
            parts.Add("RECENT EVENTS:");
            foreach (var evt in contextEvents) {
                parts.Add(evt switch {
                    StoryEvent.PlayerSpeak ps => $"{ps.PlayerCharacterId} says: {ps.Text}",
                    StoryEvent.NpcSpeak ns => $"{ns.NpcCharacterId} says: {ns.Text}",
                    _ => evt.ToString()!,
                });
            }
        }

        return parts.Count > 0 ? string.Join("\n", parts) : null;
    }
}
