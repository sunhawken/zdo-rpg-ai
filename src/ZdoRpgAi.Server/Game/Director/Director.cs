using ZdoRpgAi.Core;
using ZdoRpgAi.Protocol.Messages;
using ZdoRpgAi.Protocol.Rpc;
using ZdoRpgAi.Server.Game.Npc;
using ZdoRpgAi.Server.Game.Story;
using ZdoRpgAi.Server.Llm;
using ZdoRpgAi.Server.Util.Mp3;

namespace ZdoRpgAi.Server.Game.Director;

public class Director {
    private static readonly ILog Log = Logger.Get<Director>();

    // Hard cap on unprompted NPC-to-NPC exchanges (A speaks to B, B replies to A, stop) so a
    // chain can't run away on its own -- resets whenever the player speaks.
    private const int MaxAmbientChainDepth = 2;

    private readonly Story.Story _story;
    private readonly DirectorHelper _directorHelper;
    private readonly NpcSpeechGenerator _npcSpeechGenerator;
    private readonly SimpleReactiveStrategy _simpleReactive;
    private readonly IRpcChannel _rpc;
    private readonly NpcRepository _npcRepo;
    private readonly WorldState _worldState;
    private readonly object _bufferLock = new();
    private readonly List<StoryEvent> _buffer = [];
    private bool _processing;
    private int _playerInterruptionIteration = 0;
    private int _ambientChainDepth = 0;

    public Director(Story.Story story, DirectorHelper directorHelper, NpcSpeechGenerator npcSpeechGenerator, IRpcChannel rpc, ILlm mainLlm, ILlm simpleLlm, NpcRepository npcRepo, WorldState worldState) {
        _story = story;
        _directorHelper = directorHelper;
        _npcSpeechGenerator = npcSpeechGenerator;
        _rpc = rpc;
        _npcRepo = npcRepo;
        _worldState = worldState;
        _simpleReactive = new SimpleReactiveStrategy(mainLlm, simpleLlm, story, npcRepo, rpc, worldState);
        story.EventRegistered += OnStoryEventRegistered;
    }

    private void OnStoryEventRegistered(StoryEvent evt) {
        Log.Debug("OnStoryEventRegistered");

        if (evt is StoryEvent.PlayerSpeak) {
            // The player has started a new utterance -- bump the iteration so anything currently
            // sitting in WaitUnlessInterruptedAsync (holding the drain loop open for a previous
            // NPC's speech-playback duration) bails out immediately instead of making the player
            // wait behind audio that already started playing. Buffering itself was already
            // correct (events queue up fine while _processing is true); this counter existed for
            // exactly this purpose but was never incremented anywhere.
            Interlocked.Increment(ref _playerInterruptionIteration);
            _ambientChainDepth = 0;
        }

        lock (_bufferLock) {
            _buffer.Add(evt);
            if (_processing) {
                Log.Trace("Buffered event {EventType} while processing", evt.GetType().Name);
                return;
            }

            _processing = true;
        }

        Log.Trace("Starting drain for event {EventType}", evt.GetType().Name);
        _ = DrainBufferAsync();
    }

    /// <summary>
    /// Entry point for the ambient dialogue scheduler: attempts to start a spontaneous NPC-to-NPC
    /// exchange. Skips (rather than queues) if the director is already busy -- this is flavor,
    /// not something worth delaying real player/NPC activity for, and the scheduler will just try
    /// again on its next tick. Reuses the same buffer/_processing gate as every other event path
    /// so the two never run concurrently: sets _processing itself (mirroring what
    /// OnStoryEventRegistered does), registers+voices the opening line directly, then drains
    /// whatever queued up while that was happening -- in particular the target's own reply, whose
    /// registration will have found _processing already true and simply buffered itself instead
    /// of recursing, exactly like any other buffered event.
    /// </summary>
    public async Task TryStartAmbientDialogueAsync(string speakerId, string targetId) {
        lock (_bufferLock) {
            if (_processing) {
                Log.Trace("Skipping ambient dialogue attempt, director busy");
                return;
            }
            _processing = true;
        }

        try {
            var speakerInfo = await _npcRepo.GetNpcInfoAsync(speakerId);
            var targetInfo = await _npcRepo.GetNpcInfoAsync(targetId);
            if (speakerInfo == null || targetInfo == null) {
                Log.Trace("Ambient dialogue: missing info for {Speaker} or {Target}", speakerId, targetId);
                return;
            }

            var line = await _simpleReactive.GenerateAmbientOpenerAsync(speakerInfo, targetInfo);
            if (string.IsNullOrWhiteSpace(line)) {
                Log.Trace("Ambient dialogue: empty opener from {Speaker}", speakerId);
                return;
            }

            _ambientChainDepth = 0;
            var evt = StoryEvent.Create(new StoryEvent.NpcSpeak {
                NpcCharacterId = speakerId,
                TargetCharacterId = targetId,
                GameTime = StoryEvent.GetRealTime(),
                Text = line,
            });
            Log.Info("Ambient dialogue: {Speaker} -> {Target}: {Text}", speakerId, targetId, line);
            await RegisterAndPublishAsync([evt]);
        }
        catch (Exception ex) {
            Log.Error("TryStartAmbientDialogueAsync failed: {Error}", ex.Message);
        }
        finally {
            await DrainBufferAsync();
        }
    }

    private async Task DrainBufferAsync() {
        while (true) {
            List<StoryEvent> batch;
            lock (_bufferLock) {
                if (_buffer.Count == 0) {
                    _processing = false;
                    Log.Trace("Buffer drained, processing complete");
                    return;
                }

                batch = [.. _buffer];
                _buffer.Clear();
            }

            if (batch.Count > 0) {
                Log.Trace("Draining batch of {Count} events", batch.Count);
                await ProcessStoryEventsAsync(batch);
            }
        }
    }

    private async Task ProcessStoryEventsAsync(List<StoryEvent> events) {
        Log.Trace("Received {Count} story events", events.Count);

        var strategy = DetermineStrategy(events);
        if (strategy == null) {
            Log.Trace("No strategy decided");
            return;
        }

        Log.Trace("Using strategy {Strategy}", strategy.GetType().Name);
        try {
            var newEvents = await strategy.ProcessStoryEventsAsync(events);
            Log.Trace("Strategy returned {Count} new events", newEvents.Count);
            await RegisterAndPublishAsync(newEvents);
        }
        catch (Exception ex) {
            Log.Error("Strategy {Strategy} failed: {Error}", strategy.GetType().Name, ex.Message);
        }
    }

    private async Task RegisterAndPublishAsync(List<StoryEvent> events) {
        Log.Trace("Registering and publishing {Count} events", events.Count);
        var observersCache = new Dictionary<string, string[]>();
        StoryEvent.NpcSpeak? npcSpeakEvent = null;

        foreach (var e in events) {
            var (mainCharId, targetCharId) = e switch {
                StoryEvent.PlayerSpeak ps => (ps.PlayerCharacterId, ps.TargetCharacterId),
                StoryEvent.NpcSpeak ns => (ns.NpcCharacterId, ns.TargetCharacterId),
                _ => ((string?)null, (string?)null),
            };

            if (mainCharId == null) {
                _story.RegisterEvent(e, []);
                continue;
            }

            if (!observersCache.TryGetValue(mainCharId, out var observerIds)) {
                observerIds = await _directorHelper.QueryObserverIdsAsync(mainCharId, targetCharId != null ? [targetCharId] : null);
                observersCache[mainCharId] = observerIds;
            }

            _story.RegisterEvent(e, observerIds);

            if (e is StoryEvent.NpcSpeak npcSpeak) {
                Log.Info("NPC {NpcId} speaks: {Text}", npcSpeak.NpcCharacterId, npcSpeak.Text);
                npcSpeakEvent = npcSpeak;
            }
        }

        if (npcSpeakEvent != null) {
            var npc = await _npcRepo.GetNpcInfoAsync(npcSpeakEvent.NpcCharacterId);
            if (npc != null) {
                var iterationBeforeGeneration = _playerInterruptionIteration;
                Log.Trace("Generating speech for NPC {NpcId}", npcSpeakEvent.NpcCharacterId);
                var mp3 = await _npcSpeechGenerator.GenerateAsync(npc, npcSpeakEvent.Text);
                if (_playerInterruptionIteration == iterationBeforeGeneration && mp3 != null) {
                    Log.Trace("Publishing MP3 for NPC {NpcId}", npcSpeakEvent.NpcCharacterId);
                    _directorHelper.PublishNpcSpeaksMp3(npcSpeakEvent.NpcCharacterId, npcSpeakEvent.Text, mp3);

                    var durationMs = (int)((Mp3Duration.Estimate(mp3.Mp3Bytes) ?? 0) * 1000);
                    if (durationMs > 0) {
                        Log.Trace("Waiting {DurationMs}ms for speech playback", durationMs);
                        await WaitUnlessInterruptedAsync(durationMs, iterationBeforeGeneration);
                    }
                }
                else if (_playerInterruptionIteration != iterationBeforeGeneration) {
                    Log.Trace("Speech generation interrupted by player for NPC {NpcId}", npcSpeakEvent.NpcCharacterId);
                }
            }
            else {
                Log.Warn($"Cannot get NPC info id={npcSpeakEvent.NpcCharacterId}");
            }
        }
    }

    private async Task WaitUnlessInterruptedAsync(int durationMs, int expectedIteration) {
        const int pollIntervalMs = 100;
        var remaining = durationMs;
        while (remaining > 0) {
            if (_playerInterruptionIteration != expectedIteration) {
                Log.Debug("Speech playback wait interrupted by player");
                return;
            }

            var delay = Math.Min(remaining, pollIntervalMs);
            await Task.Delay(delay);
            remaining -= delay;
        }
    }

    private IDirectorStrategy? DetermineStrategy(List<StoryEvent> events) {
        var last = events.Last();
        Log.Trace("Determining strategy for last event type {EventType}", last.GetType().Name);

        if (last is StoryEvent.PlayerSpeak) {
            return _simpleReactive;
        }

        // An NPC speaking to another NPC (not the player) is an ambient exchange -- let it
        // continue for a couple of turns so the target can react, then stop.
        if (last is StoryEvent.NpcSpeak { TargetCharacterId: not null } ns
            && !_worldState.IsPlayer(ns.TargetCharacterId)
            && _ambientChainDepth < MaxAmbientChainDepth) {
            _ambientChainDepth++;
            Log.Trace("Continuing ambient NPC-NPC exchange (depth {Depth}/{Max})", _ambientChainDepth, MaxAmbientChainDepth);
            return _simpleReactive;
        }

        return null;
    }
}
