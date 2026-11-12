using Microsoft.Extensions.Options;

namespace CameraSpeechMatch.AI.Ollama;

internal sealed class OllamaSystemOneService(
    OllamaClient client, IOptions<OllamaSettings> options, OllamaReadiness readiness) : ISystemOneService
{
    public bool IsAvailable => readiness.IsReady;

    public async Task<double> MatchProbabilityAsync(MatchProbabilityRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = SystemOneProtocol.CreatePayload(request, options.Value.MatchingModel, options.Value.MaximumMatchingRequestBytes);
        if (!IsAvailable) throw new InvalidOperationException(readiness.Status);
        using var response = await client.SendAsync(HttpMethod.Post, "v1/systemone", payload,
            options.Value.MatchingTimeoutSeconds, cancellationToken);
        return SystemOneProtocol.ReadProbability(response.RootElement, options.Value.MinimumDecisionProbability, "Ollama");
    }
}
