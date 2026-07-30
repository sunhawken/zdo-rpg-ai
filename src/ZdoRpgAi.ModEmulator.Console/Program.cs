using ZdoRpgAi.Core;
using ZdoRpgAi.ModEmulator.Console;
using ZdoRpgAi.Util;

var parser = new CommandLineArgsParser("Zdo RPG AI Mod Emulator", BuildInfo.Version);
parser.Add("--host", "Host to listen on", defaultValue: "localhost");
parser.Add("-p", "--port", "Port to listen on", defaultValue: "8081");
parser.Add("--auto-say", "Non-interactive smoke test: once a client connects, say this to the default target and exit after the reply", defaultValue: "");

var parsed = parser.Parse(args);
var host = parsed.Get("--host")!;
var port = int.Parse(parsed.Get("--port")!);
var autoSay = parsed.Get("--auto-say");

Logger.Configure(new LogConfig { ConsoleLevel = LogLevel.Debug });
var log = Logger.Get<EmulatorServer>();
log.Info("Mod Emulator {Version}", BuildInfo.Version);

log.Info("World: Seyda Neen");
log.Info("NPCs:");
foreach (var npc in SeydaNeenWorld.Npcs) {
    log.Info("  {ObjectId}: {Name} ({Race}, {Sex})", npc.ObjectId, npc.Name, npc.Race, npc.Sex);
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => {
    e.Cancel = true;
    cts.Cancel();
};

var server = new EmulatorServer(host, port);
log.Info("Listening on {Host}:{Port}", host, port);

var serverTask = server.RunAsync(cts.Token);

if (!string.IsNullOrEmpty(autoSay)) {
    // Non-interactive smoke test: skip the raw-console REPL entirely (it doesn't play well
    // with redirected/automated stdin) and just wait for a client to connect, say the line,
    // then give it time to log the NPC's reply before exiting on its own.
    _ = Task.Run(async () => {
        log.Info("[auto-say] Waiting for client to connect...");
        while (server.Session == null && !cts.Token.IsCancellationRequested) {
            await Task.Delay(200, cts.Token).ConfigureAwait(false);
        }
        if (cts.Token.IsCancellationRequested) return;

        await Task.Delay(5000, cts.Token).ConfigureAwait(false); // let PlayerAdded/default target settle, and give the client's separate server websocket connection time to finish too
        log.Info("[auto-say] Sending: \"{Text}\"", autoSay);
        server.Session!.SendPlayerSpeaksText(autoSay);

        await Task.Delay(30000, cts.Token).ConfigureAwait(false); // window for LLM+TTS round trip
        log.Info("[auto-say] Done, shutting down");
        cts.Cancel();
    }, cts.Token);

    try {
        await serverTask;
    }
    catch (OperationCanceledException) {
        // Normal shutdown
    }
    return;
}

// Interactive command loop
var input = new ConsoleInput();
_ = Task.Run(async () => {
    await Task.Delay(500, cts.Token).ConfigureAwait(false);
    PrintHelp();

    while (!cts.Token.IsCancellationRequested) {
        var line = await Task.Run(() => input.ReadLine(), cts.Token).ConfigureAwait(false);
        if (line == null) break;

        var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) continue;

        var session = server.Session;
        if (session == null && parts[0] != "help" && parts[0] != "q") {
            log.Warn("No client connected");
            continue;
        }

        switch (parts[0].ToLowerInvariant()) {
            case "target" or "t":
                if (parts.Length < 2) {
                    log.Info("Usage: target <npc_id | clear>");
                } else if (parts[1] == "clear") {
                    session!.SetTarget(null);
                } else {
                    session!.SetTarget(parts[1]);
                }
                break;
            case "say" or "s":
                if (parts.Length < 2) {
                    log.Info("Usage: say <text>");
                } else {
                    session!.SendPlayerSpeaksText(parts[1]);
                }
                break;
            case "npcs":
                foreach (var npc in SeydaNeenWorld.Npcs) {
                    log.Info("  {ObjectId}: {Name} ({Race}, {Sex})", npc.ObjectId, npc.Name, npc.Race, npc.Sex);
                }
                break;
            case "help":
                PrintHelp();
                break;
            case "q":
                cts.Cancel();
                break;
            default:
                log.Warn("Unknown command: {Cmd}. Type 'help' for commands.", parts[0]);
                break;
        }
    }
}, cts.Token);

try {
    await serverTask;
}
catch (OperationCanceledException) {
    // Normal shutdown
}

void PrintHelp() {
    log.Info("Commands:");
    log.Info("  say <text>       - Send player speech text to server (alias: s)");
    log.Info("  target <npc_id>  - Set player target NPC (alias: t)");
    log.Info("  target clear     - Clear player target");
    log.Info("  npcs             - List available NPCs");
    log.Info("  help             - Show this help");
    log.Info("  q                - Quit");
}
