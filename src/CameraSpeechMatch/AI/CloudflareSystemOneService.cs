using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CameraSpeechMatch.AI;

internal sealed class CloudflareSystemOneService(AiHttpClient client, CameraSpeechMatchSemanticMatchingOptions settings) : ISystemOneService
{
    // Conservative byte budget leaves space in Clef Flash's context for its decision template.
    internal const int MaximumRequestBytes = 16 * 1024;
    public bool IsAvailable => true;

    public async Task<double> MatchProbabilityAsync(MatchProbabilityRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = SystemOneProtocol.CreatePayload(request, settings.Model!,
            Math.Min(settings.MaximumMatchingRequestBytes, MaximumRequestBytes));
        string path = $"accounts/{Uri.EscapeDataString(settings.AccountId!)}/ai/run/@cf/cloudflare/{settings.Model}";
        using var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload, options: AiJson.Options)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        using var response = await client.SendAsync(message, settings.TimeoutSeconds, cancellationToken);
        var root = response.RootElement;
        if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() != 0 ||
            !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Cloudflare returned an unsuccessful or malformed decision response.");
        return SystemOneProtocol.ReadProbability(result, settings.MinimumDecisionProbability, "Cloudflare");
    }
}
