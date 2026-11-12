using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace CameraSpeechMatch.AI;

internal sealed class JevSystemOneService(AiHttpClient client, CameraSpeechMatchSemanticMatchingOptions settings) : ISystemOneService
{
    public bool IsAvailable => true;

    public async Task<double> MatchProbabilityAsync(MatchProbabilityRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = SystemOneProtocol.CreatePayload(request, settings.Model!, settings.MaximumMatchingRequestBytes);
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/systemone")
        {
            Content = JsonContent.Create(payload, options: AiJson.Options)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        using var response = await client.SendAsync(message, settings.TimeoutSeconds, cancellationToken);
        return SystemOneProtocol.ReadProbability(response.RootElement, settings.MinimumDecisionProbability, "Jev");
    }
}
