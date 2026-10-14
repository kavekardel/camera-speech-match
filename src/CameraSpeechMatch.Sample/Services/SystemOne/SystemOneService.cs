using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using CameraSpeechMatch;
using CameraSpeechMatchSample.Configuration;
using Microsoft.Extensions.Options;

namespace CameraSpeechMatchSample.Services.SystemOne;

public sealed class SystemOneService(
    HttpClient httpClient,
    IOptions<SystemOneSettings> options,
    ILogger<SystemOneService> logger) : ISystemOneService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.Strict,
        AllowDuplicateProperties = false
    };

    public bool IsAvailable => options.Value.Enabled && httpClient.BaseAddress is { Scheme: "http" or "https" };

    public async Task<double> MatchProbabilityAsync(SystemOneMatchProbabilityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable)
        {
            throw new InvalidOperationException("System One is disabled or its gateway is not configured.");
        }
        if (string.IsNullOrWhiteSpace(request.Transcript) || string.IsNullOrWhiteSpace(request.ExpectedText) ||
            string.IsNullOrWhiteSpace(request.Task) || string.IsNullOrWhiteSpace(request.TrueCriterion) ||
            string.IsNullOrWhiteSpace(request.FalseCriterion) || request.Rules is null || request.Rules.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("System One requires comparison text and valid instructions and criteria.", nameof(request));
        }

        // Preserve Intra's boolean wire contract, including structured guidance.
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            State = request.Transcript,
            Instructions = new { request.Task, request.ExpectedText, request.Rules },
            Criteria = new { True = request.TrueCriterion, False = request.FalseCriterion }
        }, JsonOptions);
        if (payload.Length > Math.Max(1, options.Value.MaxRequestBytes))
        {
            throw new ArgumentException("The System One decision request is too large.", nameof(request));
        }

        var stopwatch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Value.TimeoutSeconds, 1, 120)));
        var token = deadline.Token;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "api/system-one/v1/boolean")
            {
                Content = new ByteArrayContent(payload)
            };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException("The System One gateway rejected the request.", null, response.StatusCode);
            }
            int limit = Math.Max(1, options.Value.MaxResponseBytes);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > limit)
            {
                throw new InvalidDataException("The System One gateway returned an unusable response.");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            byte[] bytes = await ReadBoundedAsync(stream, limit, token);
            token.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
            var result = document.RootElement.Deserialize<BooleanResponse>(JsonOptions);
            if (result is null || !double.IsFinite(result.Probability) || result.Probability is < 0 or > 1 ||
                result.Usage is not { InputTokens: >= 0, OutputTokens: >= 0 } usage ||
                usage.CostUsd is { } cost && (!double.IsFinite(cost) || cost < 0))
            {
                throw new InvalidDataException("The System One gateway returned an invalid response.");
            }
            token.ThrowIfCancellationRequested();
            logger.LogInformation(
                "System One boolean completed in {ElapsedMs} ms; request bytes {RequestBytes}, response bytes {ResponseBytes}, input tokens {InputTokens}, output tokens {OutputTokens}, cost USD {CostUsd}",
                stopwatch.ElapsedMilliseconds, payload.Length, bytes.Length, usage.InputTokens, usage.OutputTokens, usage.CostUsd);
            return result.Probability;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            LogFailure("timeout");
            throw new TimeoutException("The System One gateway timed out.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning("System One boolean failed after {ElapsedMs} ms; HTTP status {StatusCode}",
                stopwatch.ElapsedMilliseconds, exception.StatusCode is { } status ? (int)status : (int?)null);
            throw new HttpRequestException("The System One gateway request failed.", null, exception.StatusCode);
        }
        catch (JsonException)
        {
            LogFailure("invalid-json");
            throw new InvalidDataException("The System One gateway returned malformed JSON.");
        }
        catch (InvalidDataException)
        {
            LogFailure("invalid-response");
            throw new InvalidDataException("The System One gateway returned an invalid response.");
        }
        catch (IOException)
        {
            LogFailure("interrupted-connection");
            throw new HttpRequestException("The System One gateway connection was interrupted.");
        }

        void LogFailure(string category) => logger.LogWarning(
            "System One boolean failed after {ElapsedMs} ms; category {Category}", stopwatch.ElapsedMilliseconds, category);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken token)
    {
        using var output = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(8_192, limit));
        try
        {
            while (true)
            {
                int remaining = limit - (int)output.Length;
                int toRead = remaining >= buffer.Length ? buffer.Length : remaining + 1;
                int read = await stream.ReadAsync(buffer.AsMemory(0, toRead), token);
                if (read == 0)
                {
                    return output.ToArray();
                }
                if (read > remaining)
                {
                    throw new InvalidDataException("The System One response exceeds the byte limit.");
                }
                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private sealed record BooleanResponse(
        [property: JsonRequired] double Probability,
        [property: JsonRequired] UsageResponse Usage);

    private sealed record UsageResponse(
        [property: JsonRequired] int InputTokens,
        [property: JsonRequired] int OutputTokens,
        double? CostUsd);
}
