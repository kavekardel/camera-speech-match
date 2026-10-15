using System.Text.Json;
using CameraSpeechMatch;
using CameraSpeechMatchSample.Configuration;
using Microsoft.Extensions.Options;

namespace CameraSpeechMatchSample.Services.Ollama;

public sealed class OllamaSystemOneService(
    OllamaClient client, IOptions<OllamaSettings> options, OllamaReadiness readiness) : ISystemOneService
{
    public bool IsAvailable => readiness.IsReady;

    public async Task<double> MatchProbabilityAsync(MatchProbabilityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var state = SerializeContent(request.State, nameof(request.State));
        var instructions = SerializeContent(request.Instructions, nameof(request.Instructions));
        if (!IsAvailable) throw new InvalidOperationException(readiness.Status);
        // Jev scores typed answers directly; no natural-language output interpretation is involved.
        var question = new Dictionary<string, object>
        {
            ["type"] = "noul",
            ["instructions"] = instructions
        };
        var criteria = new Dictionary<string, string>();
        if (request.TrueCriteria is { } trueCriteria) criteria["true"] = trueCriteria;
        if (request.FalseCriteria is { } falseCriteria) criteria["false"] = falseCriteria;
        if (criteria.Count > 0) question["criteria"] = criteria;
        var payload = new
        {
            model = options.Value.MatchingModel,
            state,
            questions = new { match = question }
        };
        if (JsonSerializer.SerializeToUtf8Bytes(payload, OllamaClient.JsonOptions).Length > options.Value.MaximumMatchingRequestBytes)
            throw new ArgumentException("The Ollama decision request exceeds its byte limit.", nameof(request));
        using var response = await client.SendAsync(HttpMethod.Post, "v1/systemone", payload,
            options.Value.MatchingTimeoutSeconds, cancellationToken);
        var root = response.RootElement;
        if (!root.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object ||
            !answers.TryGetProperty("match", out var match) || match.ValueKind != JsonValueKind.Object ||
            !match.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "noul" ||
            !match.TryGetProperty("noul", out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double probability) || !double.IsFinite(probability) || probability is < 0 or > 1)
            throw new InvalidDataException("Ollama returned an invalid typed match probability.");
        if (probability == 0.5 || Math.Max(probability, 1 - probability) < options.Value.MinimumDecisionProbability)
            throw new InvalidDataException("Ollama could not make a sufficiently certain decision.");
        return probability;
    }

    private static JsonElement SerializeContent(object? content, string field)
    {
        JsonElement value;
        try
        {
            value = JsonSerializer.SerializeToElement(content, OllamaClient.JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new ArgumentException($"{field} must be valid JSON-serializable content.", field, exception);
        }
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ||
            value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            return value;
        throw new ArgumentException($"{field} must be a nonblank string, object, or array.", field);
    }
}
