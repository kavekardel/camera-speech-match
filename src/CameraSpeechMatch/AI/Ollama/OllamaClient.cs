using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CameraSpeechMatch.AI.Ollama;

internal sealed class OllamaClient(HttpClient httpClient, IOptions<OllamaSettings> options)
{
    public static JsonSerializerOptions JsonOptions => AiJson.Options;

    public async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? payload,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null) request.Content = JsonContent.Create(payload, options: JsonOptions);
        var client = new AiHttpClient(httpClient, options.Value.MaximumResponseBytes, "Ollama");
        return await client.SendAsync(request, timeoutSeconds, cancellationToken);
    }
}
