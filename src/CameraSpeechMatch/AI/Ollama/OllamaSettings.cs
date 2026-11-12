namespace CameraSpeechMatch.AI.Ollama;

internal sealed class OllamaSettings
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string SttModel { get; set; } = "frozenlab/qwen3-asr:0.6b";
    public string MatchingModel { get; set; } = "nimble:9b-q4_K_M";
    public int TranscriptionTimeoutSeconds { get; set; } = 120;
    public int MatchingTimeoutSeconds { get; set; } = 120;
    public int StartupTimeoutSeconds { get; set; } = 60;
    public int ProbeTimeoutSeconds { get; set; } = 5;
    public int RetryDelaySeconds { get; set; } = 2;
    public int MaximumAudioBytes { get; set; } = 10 * 1024 * 1024;
    public int MaximumResponseBytes { get; set; } = 262_144;
    public int MaximumMatchingRequestBytes { get; set; } = 65_536;
    // Reject uncertain decisions even if a caller lowers the component's acceptance threshold.
    public double MinimumDecisionProbability { get; set; } = 0.8;
}
