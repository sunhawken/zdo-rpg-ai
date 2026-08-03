using ZdoRpgAi.Core;
using ZdoRpgAi.Server.Llm.Gemini;
using ZdoRpgAi.Server.Llm.OpenAi;
using ZdoRpgAi.Server.SpeechToText.Deepgram;
using ZdoRpgAi.Server.SpeechToText.OpenAi;
using ZdoRpgAi.Server.TextToSpeech.ElevenLabs;
using ZdoRpgAi.Server.TextToSpeech.OpenAi;
using ZdoRpgAi.Server.Util.Mp3;

namespace ZdoRpgAi.Server.Bootstrap;

public class ServerConfig {
    public LogConfig Log { get; set; } = new();
    public required DatabaseSection Database { get; set; }
    public HttpServerSection HttpServer { get; set; } = new();
    public required TtsSection Tts { get; set; }
    public required SttSection Stt { get; set; }
    public required LlmSection Llm { get; set; }
    public DirectorSection Director { get; set; } = new();
}

public class DirectorSection {
    public int CompactThreshold { get; set; } = 30;
    public int CompactKeepRecent { get; set; } = 10;
    public AmbientDialogueSection AmbientDialogue { get; set; } = new();
}

public class AmbientDialogueSection {
    public bool Enabled { get; set; } = true;
    // How often the scheduler rolls the dice on starting an ambient exchange.
    public int CheckIntervalSec { get; set; } = 45;
    // Chance per check that an exchange actually starts, given 2+ NPCs are near the player.
    public double ChancePerCheck { get; set; } = 0.15;
}

public class DatabaseSection {
    public required string MainDbPath { get; set; }
    public required string SaveGameDbPath { get; set; }
}

public class HttpServerSection {
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8080;
    public int MaxMessageSize { get; set; } = 10_485_760;
    public int RpcTimeoutMs { get; set; } = 5000;
    public string ClientToken { get; set; } = "";
}

public class TtsSection {
    public required string Provider { get; set; }
    public ElevenLabsConfig? ElevenLabs { get; set; }
    public OpenAiTtsConfig? OpenAi { get; set; }
    public Mp3SpeedConfig Mp3Speed { get; set; } = new();
}

public class SttSection {
    public required string Provider { get; set; }
    public DeepgramConfig? Deepgram { get; set; }
    public OpenAiSttConfig? OpenAi { get; set; }
}

public class LlmSection {
    public required LlmProviderSection Main { get; set; }
    public required LlmProviderSection Simple { get; set; }
    // Optional, separate model for combat barks -- falls back to Simple if not set. Exists because
    // OpenAI's own moderation (proxied through by at least the "openai" provider on NanoGPT) reliably
    // rejects any prompt that names two characters as being "in combat"/"confrontation" with each
    // other, even for completely mundane one-line game dialogue -- open-weight models routed through
    // the same NanoGPT key (tested: meta-llama/llama-3.1-70b-instruct) don't hit this at all.
    public LlmProviderSection? Combat { get; set; }
}

public class LlmProviderSection {
    public required string Provider { get; set; }
    public GeminiConfig? Gemini { get; set; }
    public OpenAiConfig? OpenAi { get; set; }
}
