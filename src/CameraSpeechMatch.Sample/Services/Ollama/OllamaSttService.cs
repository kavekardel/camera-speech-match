using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using CameraSpeechMatch;
using CameraSpeechMatchSample.Configuration;
using Microsoft.Extensions.Options;

namespace CameraSpeechMatchSample.Services.Ollama;

public sealed class OllamaSttService(
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
        byte[] audio = request.PcmAudioContent ?? throw new ArgumentException("Mono 16 kHz PCM16 audio is required.", nameof(request));
        if (audio.Length == 0 || audio.Length % 2 != 0 || audio.Length > options.Value.MaximumAudioBytes)
            throw new ArgumentException("Audio must contain complete PCM16 samples within the configured byte limit.", nameof(request));
        // Containers are not raw PCM. The public request contract supplies the format, not file metadata.
        if (audio.AsSpan().StartsWith("RIFF"u8) || audio.AsSpan().StartsWith("OggS"u8) ||
            audio.AsSpan().StartsWith("ID3"u8) || audio.AsSpan().StartsWith(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }))
            throw new ArgumentException("Supply raw mono 16 kHz PCM16, not WAV, Ogg, MP3, or WebM.", nameof(request));
        if (!IsAvailable) throw new InvalidOperationException(readiness.Status);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Value.TranscriptionTimeoutSeconds));
        var transcript = new StringBuilder();
        try
        {
            for (int offset = 0; offset < audio.Length; offset += ChunkBytes)
            {
                var pcm = audio.AsSpan(offset, Math.Min(ChunkBytes, audio.Length - offset));
                // One bounded PCM copy adds a WAV header. JsonContent writes byte[] as base64 directly.
                byte[] wav = new byte[44 + pcm.Length];
                "RIFF"u8.CopyTo(wav);
                BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
                "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
                BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
                BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
                BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
                BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 16_000);
                BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), BytesPerSecond);
                BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2);
                BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
                "data"u8.CopyTo(wav.AsSpan(36));
                BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), pcm.Length);
                pcm.CopyTo(wav.AsSpan(44));
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
