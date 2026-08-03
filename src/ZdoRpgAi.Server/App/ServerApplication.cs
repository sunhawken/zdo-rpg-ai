using ZdoRpgAi.Core;
using ZdoRpgAi.Protocol.Channel;
using ZdoRpgAi.Protocol.Rpc;
using ZdoRpgAi.Repository;
using ZdoRpgAi.Server.Bootstrap;
using ZdoRpgAi.Server.Http;
using ZdoRpgAi.Server.Llm;
using ZdoRpgAi.Server.Lua;
using ZdoRpgAi.Server.SpeechToText;
using ZdoRpgAi.Server.Util.Mp3;
using ZdoRpgAi.Server.TextToSpeech;

namespace ZdoRpgAi.Server.App;

public class ServerApplication : IDisposable {
    private static readonly ILog Log = Logger.Get<ServerApplication>();

    private readonly Game.GameRunner _game;
    private readonly HttpServer _httpServer;
    private readonly IMainRepository _mainRepo;
    private readonly ISaveGameRepository _saveGameRepo;
    private readonly ISpeechToText _stt;
    private readonly object _lock = new();

    private RpcChannel? _activeRpc;
    private ReusableRpcChannel _reusableRpc = new();

    public ServerApplication(
        IMainRepository mainRepo, ISaveGameRepository saveGameRepo,
        ITextToSpeech tts, ISpeechToText stt, ILlm mainLlm, ILlm simpleLlm, ILlm combatLlm, LuaSandbox lua,
        HttpServer httpServer, DirectorSection directorConfig, Mp3SpeedConfig mp3SpeedConfig) {
        _mainRepo = mainRepo;
        _saveGameRepo = saveGameRepo;
        _stt = stt;
        _httpServer = httpServer;
        _game = new Game.GameRunner(mainRepo, saveGameRepo, _reusableRpc, tts, stt, mainLlm, simpleLlm, combatLlm, lua, directorConfig, mp3SpeedConfig);

        _httpServer.ClientConnected += OnClientConnected;
    }

    public async Task RunAsync(CancellationToken ct) {
        Log.Info("Server started");
        await Task.WhenAll(_httpServer.StartAsync(ct), _game.RunAsync(ct));
        Log.Info("Server stopped");
    }

    public void Dispose() {
        _stt.Dispose();
        _saveGameRepo.Dispose();
        _mainRepo.Dispose();
    }

    private void OnClientConnected(IChannel rpc) {
        lock (_lock) {
            _activeRpc?.Close();

            _activeRpc = new RpcChannel(rpc);
            _reusableRpc.Underlying = _activeRpc;
        }

        rpc.Disconnected += () => {
            lock (_lock) {
                if (_activeRpc == rpc) {
                    _activeRpc = null;
                    _reusableRpc.Underlying = null;
                }
            }
        };
    }
}
