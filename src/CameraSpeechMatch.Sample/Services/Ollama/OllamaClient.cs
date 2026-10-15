using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CameraSpeechMatchSample.Configuration;
using Microsoft.Extensions.Options;

namespace CameraSpeechMatchSample.Services.Ollama;

public sealed class OllamaClient(HttpClient httpClient, IOptions<OllamaSettings> options)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false
    };

    public async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? payload,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (payload is not null) request.Content = JsonContent.Create(payload, options: JsonOptions);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Ollama {path} returned HTTP {(int)response.StatusCode}. Check Ollama's logs, model availability, and runtime compatibility.",
                    null, response.StatusCode);
            }
            int limit = options.Value.MaximumResponseBytes;
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > limit)
                throw new InvalidDataException("Ollama returned an unusable or oversized response.");

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var output = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit - (int)output.Length + 1)), deadline.Token);
                if (read == 0) break;
                if (output.Length + read > limit) throw new InvalidDataException("Ollama's response exceeds the byte limit.");
                output.Write(buffer, 0, read);
            }
            deadline.Token.ThrowIfCancellationRequested();
            var document = JsonDocument.Parse(output.GetBuffer().AsMemory(0, (int)output.Length),
                new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.TryGetProperty("error", out _))
            {
                document.Dispose();
                throw new InvalidDataException("Ollama returned an error or an invalid response object.");
            }
            return document;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Ollama {path} timed out after {timeoutSeconds} seconds.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Ollama returned malformed JSON.");
        }
        catch (IOException exception)
        {
            throw new HttpRequestException("The Ollama connection was interrupted.", exception);
        }
    }
}
