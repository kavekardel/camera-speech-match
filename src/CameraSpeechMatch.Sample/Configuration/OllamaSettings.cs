using System.ComponentModel.DataAnnotations;

namespace CameraSpeechMatchSample.Configuration;

public sealed class OllamaSettings
{
    public const string SectionName = "Ollama";

    [Required, Url]
    public string BaseUrl { get; set; } = "http://localhost:11434";
    [Required]
    public string SttModel { get; set; } = "frozenlab/qwen3-asr:0.6b";
    [Required]
    public string MatchingModel { get; set; } = "nimble:9b-q4_K_M";
    [Range(1, 600)]
    public int TranscriptionTimeoutSeconds { get; set; } = 120;
    [Range(1, 600)]
    public int MatchingTimeoutSeconds { get; set; } = 120;
    [Range(1, 300)]
    public int StartupTimeoutSeconds { get; set; } = 60;
    [Range(1, 30)]
    public int ProbeTimeoutSeconds { get; set; } = 5;
    [Range(1, 30)]
    public int RetryDelaySeconds { get; set; } = 2;
    [Range(2, 10 * 1024 * 1024)]
    public int MaximumAudioBytes { get; set; } = 10 * 1024 * 1024;
    [Range(1024, 1024 * 1024)]
    public int MaximumResponseBytes { get; set; } = 262_144;
    [Range(1024, 65_536)]
    public int MaximumMatchingRequestBytes { get; set; } = 65_536;
    // Reject uncertain decisions even if a caller lowers the component's acceptance threshold.
    [Range(0.5, 1)]
    public double MinimumDecisionProbability { get; set; } = 0.8;
}
