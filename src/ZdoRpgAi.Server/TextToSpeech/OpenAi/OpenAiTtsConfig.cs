namespace ZdoRpgAi.Server.TextToSpeech.OpenAi;

public class OpenAiTtsConfig {
    public required string ApiKey { get; init; }
    public string Model { get; init; } = "tts-1";
    public string BaseUrl { get; init; } = "https://api.openai.com";
    public required VoiceMappingConfig VoiceMapping { get; init; }
}

public class VoiceMappingConfig {
    public required string Fallback { get; init; }
    public Dictionary<string, string> ByRaceSex { get; init; } = new();
}
