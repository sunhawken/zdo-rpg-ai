using ZdoRpgAi.Core;
using ZdoRpgAi.Protocol.Messages;
using ZdoRpgAi.Protocol.Rpc;
using ZdoRpgAi.Repository;

namespace ZdoRpgAi.Server.Game.Npc;

public record NpcInfo(string Id, string Name, string Race, string Sex, string? Class = null);

public class NpcRepository {
    private static readonly ILog Log = Logger.Get<NpcRepository>();

    private readonly IMainRepository _mainRepo;
    private readonly ISaveGameRepository _saveGameRepo;
    private readonly IRpcChannel _rpc;

    public NpcRepository(IMainRepository mainRepo, ISaveGameRepository saveGameRepo, IRpcChannel rpc) {
        _mainRepo = mainRepo;
        _saveGameRepo = saveGameRepo;
        _rpc = rpc;
    }

    public async Task<NpcInfo?> GetNpcInfoAsync(string npcId) {
        var raw = _saveGameRepo.GetNpcInfo(npcId)
               ?? _mainRepo.GetNpcInfo(npcId);
        if (raw != null) {
            return ToNpcInfo(raw);
        }

        var response = await _rpc.CallAsync(
            nameof(ServerToModMessageType.GetNpcInfo),
            JsonExtensions.SerializeToObject(
                new GetNpcInfoRequestPayload(npcId),
                PayloadJsonContext.Default.GetNpcInfoRequestPayload));

        var payload = response.Json?.DeserializeSafe(PayloadJsonContext.Default.GetNpcInfoResponsePayload);
        if (payload == null) {
            Log.Warn("Mod returned no info for NPC {NpcId}", npcId);
            return null;
        }

        var info = new RawNpcInfo(npcId, payload.Name, payload.Race, payload.Sex, payload.Class);
        _saveGameRepo.SaveNpcInfo(info);
        return ToNpcInfo(info);
    }

    private static NpcInfo ToNpcInfo(RawNpcInfo raw) => new(raw.Id, raw.Name, raw.Race, raw.Sex, raw.Class);
}
