using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text.Json;
using OpenAI;

namespace CameraSpeechMatch.AI;

/// <summary>Owns the SDK transport wrapper, but not the named HttpClient supplied by the registry.</summary>
internal sealed class OpenAIProvider : IDisposable
{
    private readonly HttpClient transport;
    internal OpenAIClient Client { get; }

    internal OpenAIProvider(HttpClient http, Uri endpoint, CameraSpeechMatchAiOperationOptions settings, int? maximumRequestBytes = null)
    {
        transport = new HttpClient(new BoundedAiHandler(http, settings.MaximumResponseBytes,
            maximumRequestBytes, string.IsNullOrWhiteSpace(settings.ApiKey))) { Timeout = Timeout.InfiniteTimeSpan };
        Client = new(new ApiKeyCredential(string.IsNullOrWhiteSpace(settings.ApiKey) ? "unused" : settings.ApiKey),
            new OpenAIClientOptions
            {
                Endpoint = endpoint,
                Transport = new HttpClientPipelineTransport(transport),
                RetryPolicy = new ClientRetryPolicy(0),
                NetworkTimeout = Timeout.InfiniteTimeSpan
            });
    }

    internal static async Task<T> InvokeAsync<T>(Func<CancellationToken, Task<T>> action, int timeoutSeconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            T result = await action(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("The AI provider exceeded its processing deadline.");
        }
        catch (ClientResultException exception)
        {
            throw new HttpRequestException($"The AI provider returned HTTP {exception.Status}.", null,
                exception.Status > 0 ? (HttpStatusCode)exception.Status : null);
        }
        catch (HttpRequestException exception)
        {
            throw new HttpRequestException("The AI provider request failed.", null, exception.StatusCode);
        }
        catch (Exception exception) when ((exception is JsonException or FormatException or InvalidOperationException or IOException) &&
            exception is not InvalidDataException)
        {
            throw new InvalidDataException("The AI provider returned an unusable response.");
        }
    }

    public void Dispose() => transport.Dispose();
}

internal sealed class BoundedAiHandler(
    HttpClient http, int maximumResponseBytes, int? maximumRequestBytes, bool removeAuthorization) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (removeAuthorization) request.Headers.Authorization = null;
        if (maximumRequestBytes is { } requestLimit && request.Content is { } requestContent)
            request.Content = await BufferAsync(requestContent, requestLimit, cancellationToken);
        // The SDK transport has already marked its message as sent. Forward a new message
        // through the named client so its configured handlers and connection pool are reused.
        using var outgoing = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version, VersionPolicy = request.VersionPolicy, Content = request.Content
        };
        foreach (var header in request.Headers) outgoing.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in request.Options) outgoing.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        var response = await http.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"The AI provider returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
            response.Content = await BufferAsync(response.Content, maximumResponseBytes, cancellationToken);
            // Validate strict JSON before the SDK reads it, including duplicate fields.
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken),
                new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.TryGetProperty("error", out _))
                throw new InvalidDataException("The AI provider returned an error or an invalid response object.");
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static async Task<HttpContent> BufferAsync(HttpContent content, int limit, CancellationToken token)
    {
        if (content.Headers.ContentLength > limit)
            throw new InvalidDataException("The AI provider request or response exceeds its byte limit.");
        await using var stream = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit - (int)output.Length + 1)), token);
            if (read == 0) break;
            if (output.Length + read > limit)
                throw new InvalidDataException("The AI provider request or response exceeds its byte limit.");
            output.Write(buffer, 0, read);
        }
        var result = new ByteArrayContent(output.ToArray());
        foreach (var header in content.Headers) result.Headers.TryAddWithoutValidation(header.Key, header.Value);
        content.Dispose();
        return result;
    }
}
