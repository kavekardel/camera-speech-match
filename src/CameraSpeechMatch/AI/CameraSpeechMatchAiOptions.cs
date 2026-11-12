namespace CameraSpeechMatch;

public enum TranscriptionProvider
{
    None = 0,
    Ollama = 1,
    OpenAICompatible = 2,
    Deepgram = 3,
    ElevenLabs = 4
}

public enum SemanticMatchingProvider
{
    None = 0,
    OllamaSystemOne = 1,
    OpenAICompatible = 2,
    Jev = 3,
    CloudflareClef = 4
}

/// <summary>Server-side AI configuration. Each operation can use a different provider.</summary>
public sealed class CameraSpeechMatchAiOptions
{
    public const string SectionName = "CameraSpeechMatch:AI";
    public CameraSpeechMatchTranscriptionOptions Transcription { get; set; } = new();
    public CameraSpeechMatchSemanticMatchingOptions SemanticMatching { get; set; } = new();
}

public abstract class CameraSpeechMatchAiOperationOptions
{
    /// <summary>Provider base URL, including any proxy prefix. OpenAI-compatible URLs include /v1.</summary>
    public string? Endpoint { get; set; }
    public string? Model { get; set; }
    /// <summary>Keep credentials in server-side secrets or environment variables.</summary>
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
    public int MaximumResponseBytes { get; set; } = 262_144;
    public int StartupTimeoutSeconds { get; set; } = 60;
    public int ProbeTimeoutSeconds { get; set; } = 5;
    public int RetryDelaySeconds { get; set; } = 2;
}

public sealed class CameraSpeechMatchTranscriptionOptions : CameraSpeechMatchAiOperationOptions
{
    public TranscriptionProvider Provider { get; set; }
    /// <summary>Provider-supported language code, for example fa or en. Null requests automatic detection. Ollama supports null only.</summary>
    public string? Language { get; set; }
    public int MaximumAudioBytes { get; set; } = 10 * 1024 * 1024;
}

public sealed class CameraSpeechMatchSemanticMatchingOptions : CameraSpeechMatchAiOperationOptions
{
    public SemanticMatchingProvider Provider { get; set; }
    /// <summary>Cloudflare Workers AI account identifier. Required for CloudflareClef.</summary>
    public string? AccountId { get; set; }
    public int MaximumMatchingRequestBytes { get; set; } = 65_536;
    /// <summary>Native System One providers reject insufficiently certain decisions using this minimum.</summary>
    public double MinimumDecisionProbability { get; set; } = 0.8;
}
