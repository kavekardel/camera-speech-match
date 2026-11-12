using System.Text.Json;

namespace CameraSpeechMatch.AI;

internal static class SystemOneProtocol
{
    internal static JsonElement CreatePayload(MatchProbabilityRequest request, string model, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = MatchContent.Serialize(request.State, nameof(request.State));
        var instructions = MatchContent.Serialize(request.Instructions, nameof(request.Instructions));
        var question = new Dictionary<string, object> { ["type"] = "noul", ["instructions"] = instructions };
        var criteria = new Dictionary<string, string>();
        if (request.TrueCriteria is { } trueCriteria) criteria["true"] = trueCriteria;
        if (request.FalseCriteria is { } falseCriteria) criteria["false"] = falseCriteria;
        if (criteria.Count > 0) question["criteria"] = criteria;
        var payload = JsonSerializer.SerializeToElement(new { model, state, questions = new { match = question } }, AiJson.Options);
        if (JsonSerializer.SerializeToUtf8Bytes(payload, AiJson.Options).Length > maximumBytes)
            throw new ArgumentException("The System One decision request exceeds its byte limit.", nameof(request));
        return payload;
    }

    internal static double ReadProbability(JsonElement root, double minimumDecisionProbability, string providerName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object ||
            !answers.TryGetProperty("match", out var match) || match.ValueKind != JsonValueKind.Object ||
            !match.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "noul" ||
            !match.TryGetProperty("noul", out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double probability) || !double.IsFinite(probability) || probability is < 0 or > 1)
            throw new InvalidDataException($"{providerName} returned an invalid typed match probability.");
        if (probability == 0.5 || Math.Max(probability, 1 - probability) < minimumDecisionProbability)
            throw new InvalidDataException($"{providerName} could not make a sufficiently certain decision.");
        return probability;
    }
}
