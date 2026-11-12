using System.Text;
using System.Text.Json;
using CameraSpeechMatch;
using CameraSpeechMatch.AI;
using Microsoft.Extensions.Options;

namespace CameraSpeechMatch.AI.Ollama;

internal sealed class OllamaSttService(
    OllamaClient client, IOptions<OllamaSettings> options, OllamaReadiness readiness) : ISttService
{
    private const int BytesPerSecond = 16_000 * 2;
    // The model card recommends windows <= 60 seconds; 30 seconds also bounds working memory.
    private const int ChunkBytes = 30 * BytesPerSecond;
    public bool IsAvailable => readiness.IsReady;

    public async Task<string> TranscribeAsync(TranscribeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] audio = PcmAudio.Validate(request, options.Value.MaximumAudioBytes);
        if (!IsAvailable) throw new InvalidOperationException(readiness.Status);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Value.TranscriptionTimeoutSeconds));
        var transcript = new StringBuilder();
        try
        {
            for (int offset = 0; offset < audio.Length; offset += ChunkBytes)
            {
                var pcm = audio.AsSpan(offset, Math.Min(ChunkBytes, audio.Length - offset));
                byte[] wav = PcmAudio.ToWave(pcm);
                using var response = await client.SendAsync(HttpMethod.Post, "api/chat", new
                {
                    model = options.Value.SttModel,
                    stream = false,
                    messages = new[] { new { role = "user", content = "", images = new[] { wav } } },
                    options = new { temperature = 0 }
                }, options.Value.TranscriptionTimeoutSeconds, deadline.Token);
                var root = response.RootElement;
                if (!root.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True ||
                    !root.TryGetProperty("done_reason", out var reason) || reason.ValueKind != JsonValueKind.String || reason.GetString() != "stop" ||
                    !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
                    !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Ollama returned an incomplete transcription response.");
                string text = content.GetString()!;
                int marker = text.IndexOf("<asr_text>", StringComparison.Ordinal);
                if (marker < 0 || text.IndexOf("<asr_text>", marker + 10, StringComparison.Ordinal) >= 0)
                    throw new InvalidDataException("The ASR response is missing its unique <asr_text> marker.");
                text = text[(marker + 10)..].Trim();
                if (string.IsNullOrWhiteSpace(text) || text.Contains('<') || text.Contains('>'))
                    throw new InvalidDataException("The ASR model returned empty or malformed transcription text.");
                if (transcript.Length > 0) transcript.Append(' ');
                transcript.Append(text);
            }
            deadline.Token.ThrowIfCancellationRequested();
            return transcript.ToString();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Ollama transcription exceeded the total audio-processing deadline.");
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
