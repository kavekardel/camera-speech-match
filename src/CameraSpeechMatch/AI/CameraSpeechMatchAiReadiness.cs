using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CameraSpeechMatch;

/// <summary>Configuration/readiness status, not a guarantee of successful model inference.</summary>
public sealed record AiProviderStatus(bool IsConfigured, bool IsReady, string Message);

/// <summary>Provider-neutral startup status. Custom services are sampled when the host starts.</summary>
public sealed class CameraSpeechMatchAiReadiness : IHealthCheck
{
    private AiProviderStatus transcription = new(false, false, "Speech recognition is not configured.");
    private AiProviderStatus semanticMatching = new(false, false, "Semantic matching is not configured.");
    public AiProviderStatus Transcription => Volatile.Read(ref transcription);
    public AiProviderStatus SemanticMatching => Volatile.Read(ref semanticMatching);
    public event Action? Changed;

    public bool IsReadyFor(CameraSpeechMatchMode mode) =>
        Transcription.IsReady && (mode == CameraSpeechMatchMode.Lexical || SemanticMatching.IsReady);

    public string GetStatus(CameraSpeechMatchMode mode) =>
        !Transcription.IsReady ? Transcription.Message :
        mode != CameraSpeechMatchMode.Lexical && !SemanticMatching.IsReady ? SemanticMatching.Message : "AI providers are ready.";

    internal void Set(bool isTranscription, AiProviderStatus status)
    {
        var previous = isTranscription
            ? Interlocked.Exchange(ref transcription, status)
            : Interlocked.Exchange(ref semanticMatching, status);
        if (previous != status) Changed?.Invoke();
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        bool ready = Transcription.IsReady && (!SemanticMatching.IsConfigured || SemanticMatching.IsReady);
        string message = !Transcription.IsReady ? Transcription.Message :
            SemanticMatching.IsConfigured && !SemanticMatching.IsReady ? SemanticMatching.Message : "Configured AI providers are ready.";
        return Task.FromResult(ready ? HealthCheckResult.Healthy(message) : HealthCheckResult.Unhealthy(message));
    }
}
