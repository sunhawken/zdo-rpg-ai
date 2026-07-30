namespace ZdoRpgAi.Server.SpeechToText.OpenAi;

public class OpenAiSttConfig {
    public required string ApiKey { get; init; }
    public string Model { get; init; } = "whisper-1";
    public string BaseUrl { get; init; } = "https://api.openai.com";
    public int SampleRate { get; init; } = 16_000;
    public string? Language { get; init; }
}
