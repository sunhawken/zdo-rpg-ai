using ZdoRpgAi.Core;
using ZdoRpgAi.Protocol.Rpc;
using ZdoRpgAi.Repository;
using ZdoRpgAi.Server.Bootstrap;
using ZdoRpgAi.Server.Game.Director;
using ZdoRpgAi.Server.Game.Npc;
using ZdoRpgAi.Server.Util.Mp3;
using ZdoRpgAi.Server.Llm;
using ZdoRpgAi.Server.Lua;
using ZdoRpgAi.Server.SpeechToText;
using ZdoRpgAi.Server.Game.Story;
using ZdoRpgAi.Server.TextToSpeech;

namespace ZdoRpgAi.Server.Game;

public class GameRunner {
    private static readonly ILog Log = Logger.Get<GameRunner>();

    private readonly IMainRepository _mainRepo;
    private readonly ISaveGameRepository _saveGameRepo;
    private readonly ITextToSpeech _tts;
    private readonly ISpeechToText _stt;
    private readonly LuaSandbox _lua;
    private readonly PlayerMessageHandler _playerHandler;
    private readonly StoryComposer _storyComposer;
    private readonly NpcRepository _npcRepo;
    private readonly Director.Director _director;
    private readonly Director.AmbientDialogueScheduler _ambientScheduler;

    public GameRunner(
        IMainRepository mainRepo, ISaveGameRepository saveGameRepo,
        IRpcChannel rpc,
        ITextToSpeech tts, ISpeechToText stt, ILlm mainLlm, ILlm simpleLlm, ILlm combatLlm, LuaSandbox lua,
        DirectorSection directorConfig, Mp3SpeedConfig mp3SpeedConfig) {
        _mainRepo = mainRepo;
        _saveGameRepo = saveGameRepo;
        _tts = tts;
        _stt = stt;
        _lua = lua;

        var summaryBuilder = new StorySummaryBuilder(simpleLlm);
        var story = new Story.Story(saveGameRepo, summaryBuilder, directorConfig);
        var worldState = new WorldState();
        _playerHandler = new PlayerMessageHandler(stt, rpc);
        var directorHelper = new Director.DirectorHelper(rpc);
        _npcRepo = new NpcRepository(mainRepo, saveGameRepo, rpc);
        var speedAdjuster = new Mp3SpeedAdjuster(mp3SpeedConfig);
        var npcSpeechGenerator = new NpcSpeechGenerator(tts, speedAdjuster);
        _director = new Director.Director(story, directorHelper, npcSpeechGenerator, rpc, mainLlm, simpleLlm, combatLlm, _npcRepo, worldState);
        _storyComposer = new StoryComposer(story, directorHelper, worldState, _director, rpc);
        _ambientScheduler = new Director.AmbientDialogueScheduler(_director, directorHelper, worldState, directorConfig.AmbientDialogue);

        _playerHandler.PlayerSpoke += _storyComposer.OnPlayerSpeak;
    }

    public Task RunAsync(CancellationToken ct) => _ambientScheduler.RunAsync(ct);
}
