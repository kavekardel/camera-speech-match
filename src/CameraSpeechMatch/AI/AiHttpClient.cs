using System.Net;
using System.Text.Json;

namespace CameraSpeechMatch.AI;

/// <summary>Reads bounded JSON responses inside the operation's deadline. The caller owns the request and returned document.</summary>
internal sealed class AiHttpClient(HttpClient http, int maximumResponseBytes, string providerName)
{
    internal async Task<JsonDocument> SendAsync(HttpRequestMessage request, int timeoutSeconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{providerName} returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > maximumResponseBytes)
                throw new InvalidDataException($"{providerName} returned an unusable or oversized response.");

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var output = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0,
                    Math.Min(buffer.Length, maximumResponseBytes - (int)output.Length + 1)), deadline.Token);
                if (read == 0) break;
                if (output.Length + read > maximumResponseBytes)
                    throw new InvalidDataException($"{providerName}'s response exceeds the byte limit.");
                output.Write(buffer, 0, read);
            }
            deadline.Token.ThrowIfCancellationRequested();
            var document = JsonDocument.Parse(output.GetBuffer().AsMemory(0, (int)output.Length),
                new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.TryGetProperty("error", out _))
            {
                document.Dispose();
                throw new InvalidDataException($"{providerName} returned an error or an invalid response object.");
            }
            return document;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"{providerName} timed out after {timeoutSeconds} seconds.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"{providerName} returned malformed JSON.");
        }
        catch (HttpRequestException exception)
        {
            string message = exception.StatusCode is { } status
                ? $"{providerName} returned HTTP {(int)status}." : $"{providerName} request failed.";
            throw new HttpRequestException(message, null, exception.StatusCode);
        }
        catch (IOException)
        {
            throw new HttpRequestException($"The {providerName} connection was interrupted.");
        }
    }
}
