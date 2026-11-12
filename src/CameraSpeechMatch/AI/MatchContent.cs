using System.Text.Json;

namespace CameraSpeechMatch.AI;

internal static class MatchContent
{
    internal static JsonElement Serialize(object? content, string field)
    {
        JsonElement value;
        try { value = JsonSerializer.SerializeToElement(content, AiJson.Options); }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new ArgumentException($"{field} must be valid JSON-serializable content.", field);
        }
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ||
            value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            return value;
        throw new ArgumentException($"{field} must be a nonblank string, object, or array.", field);
    }
}

internal static class AiJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { AllowDuplicateProperties = false };
}
