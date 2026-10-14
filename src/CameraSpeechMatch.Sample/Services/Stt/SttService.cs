using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CameraSpeechMatch;
using CameraSpeechMatchSample.Configuration;
using Microsoft.Extensions.Options;

namespace CameraSpeechMatchSample.Services.Stt;

public sealed class SttService(
    IOptions<SttGatewaySettings> sttOptions,
    IOptions<SpeechInputSettings> speechOptions,
    ILogger<SttService> logger) : ISttService
{
    public bool IsAvailable => speechOptions.Value.Enabled &&
        Uri.TryCreate(sttOptions.Value.BaseUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" or "ws" or "wss";

    public async Task<string> TranscribeAsync(SttTranscribeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsAvailable)
        {
            throw new InvalidOperationException("Speech recognition is disabled or the service is not configured.");
        }
        if (request.Language is not ("fa" or "en"))
        {
            throw new ArgumentException("Speech recognition supports only fa and en.", nameof(request));
        }

        byte[] audio = request.PcmAudioContent ?? throw new InvalidOperationException("Enable CapturePcmAudio to use the sample STT service.");

        int maximumAudioBytes = Math.Clamp(speechOptions.Value.MaximumAudioBytes, 2, 10 * 1024 * 1024);

        if (audio.Length == 0 || audio.Length % 2 != 0 || audio.Length > maximumAudioBytes)
        {
            throw new ArgumentException("PCM audio must contain complete 16-bit samples within the byte limit.", nameof(request));
        }

        var stopwatch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(speechOptions.Value.MaximumSessionSeconds, 1, 600)));
        try
        {
            string transcript = await TranscribeCoreAsync(audio, request.Language, deadline.Token);
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogInformation("STT completed in {ElapsedMs} ms; audio bytes {AudioBytes}, transcript characters {Characters}",
                stopwatch.ElapsedMilliseconds, audio.Length, transcript.Length);
            return transcript;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("STT timed out after {ElapsedMs} ms", stopwatch.ElapsedMilliseconds);
            throw new TimeoutException("The speech recognition service timed out.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The speech recognition service returned malformed JSON.");
        }
        catch (Exception exception) when (exception is WebSocketException or IOException && exception is not InvalidDataException)
        {
            throw new HttpRequestException("The speech recognition connection was interrupted.", exception);
        }
    }

    private async Task<string> TranscribeCoreAsync(byte[] audio, string language, CancellationToken token)
    {
        var endpoint = new UriBuilder(sttOptions.Value.BaseUrl);
        endpoint.Scheme = endpoint.Scheme is "https" or "wss" ? "wss" : "ws";
        endpoint.Path = $"{endpoint.Path.TrimEnd('/')}/api/stt/v1/stream";
        using var socket = new ClientWebSocket();
        using (var connection = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            connection.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(sttOptions.Value.ConnectTimeoutSeconds, 1, 120)));
            await socket.ConnectAsync(endpoint.Uri, connection.Token);
        }
        byte[] configuration = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Language = language, AudioFormat = "pcm_s16le", SampleRate = 16_000, Channels = 1
        }, JsonSerializerOptions.Web);
        await socket.SendAsync(configuration.AsMemory(), WebSocketMessageType.Text, true, token);

        int maximumEventBytes = Math.Clamp(sttOptions.Value.MaximumEventBytes, 1, 1_048_576);
        byte[] buffer = new byte[Math.Min(maximumEventBytes, 16 * 1024)];
        using (var readiness = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            readiness.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(speechOptions.Value.ReadyTimeoutSeconds, 1, 120)));
            using var ready = await ReadDocumentAsync(readiness.Token);
            if (ReadType(ready) != "ready")
            {
                throw new InvalidDataException("The speech recognition service did not send ready.");
            }
        }

        using var exchange = CancellationTokenSource.CreateLinkedTokenSource(token);
        int completionSent = 0;
        Task<string> events = ReadTranscriptAsync();
        Task send = SendAudioAsync();
        try
        {
            Task first = await Task.WhenAny(send, events);
            await first;
            await send;
            return await events;
        }
        finally
        {
            // Release either side when its peer fails; never leave a receive/send running.
            await exchange.CancelAsync();
            socket.Abort();
            try { await Task.WhenAll(send, events); }
            catch { /* Preserve the original failure. */ }
        }

        async Task SendAudioAsync()
        {
            const int chunkBytes = 1_280; // 40 ms of mono PCM16 at 16 kHz, like Intra's browser capture.
            var audioClock = Stopwatch.StartNew();
            for (int offset = 0; offset < audio.Length; offset += chunkBytes)
            {
                int count = Math.Min(chunkBytes, audio.Length - offset);
                // This is a real-time API. Bursting a whole recording can queue audio
                // upstream past the finalization deadline; preserve Intra's delivery rate.
                TimeSpan wait = TimeSpan.FromSeconds((offset + count) / 32_000d) - audioClock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, exchange.Token);
                }
                await socket.SendAsync(audio.AsMemory(offset, count), WebSocketMessageType.Binary, true, exchange.Token);
            }
            exchange.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(speechOptions.Value.FinalizationTimeoutSeconds, 1, 120)));
            Volatile.Write(ref completionSent, 1);
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"complete\"}").AsMemory(),
                WebSocketMessageType.Text, true, exchange.Token);
        }

        async Task<string> ReadTranscriptAsync()
        {
            var transcript = new StringBuilder();
            while (true)
            {
                using var document = await ReadDocumentAsync(exchange.Token);
                string type = ReadType(document);
                if (type == "completed")
                {
                    if (Volatile.Read(ref completionSent) == 0)
                    {
                        throw new InvalidDataException("The speech recognition service completed before the audio was sent.");
                    }
                    return transcript.ToString();
                }
                if (type != "transcript" ||
                    !document.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("The speech recognition service returned an invalid event.");
                }
                bool final = false;
                if (document.RootElement.TryGetProperty("isFinal", out var isFinal))
                {
                    if (isFinal.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        throw new InvalidDataException("The speech recognition service returned an invalid final flag.");
                    }
                    final = isFinal.GetBoolean();
                }
                string fragment = text.GetString()!.Trim();
                if (final && fragment.Length > 0)
                {
                    if (transcript.Length > 0 && !".,!?:;،؛؟".Contains(fragment[0]))
                    {
                        transcript.Append(' ');
                    }
                    transcript.Append(fragment);
                    if (transcript.Length > 262_144)
                    {
                        throw new InvalidDataException("The recognized transcript is too large.");
                    }
                }
            }
        }

        async Task<JsonDocument> ReadDocumentAsync(CancellationToken readToken)
        {
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), readToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new HttpRequestException("The speech recognition connection closed before completion.");
                }
                if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > maximumEventBytes)
                {
                    throw new InvalidDataException("The speech recognition event is not text or exceeds the byte limit.");
                }
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return JsonDocument.Parse(message.ToArray(), new JsonDocumentOptions { AllowDuplicateProperties = false });
                }
            }
        }
    }

    private static string ReadType(JsonDocument document)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("The speech recognition event has no type.");
        }
        string value = type.GetString()!;
        if (value == "error")
        {
            throw new HttpRequestException("The speech recognition service rejected the session.");
        }
        return value;
    }
}
