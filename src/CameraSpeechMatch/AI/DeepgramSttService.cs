using System.Net.Http.Headers;
using System.Text.Json;

namespace CameraSpeechMatch.AI;

internal sealed class DeepgramSttService(AiHttpClient client, CameraSpeechMatchTranscriptionOptions settings) : ISttService
{
    public bool IsAvailable => true;

    public async Task<string> TranscribeAsync(TranscribeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] pcm = PcmAudio.Validate(request, settings.MaximumAudioBytes);
        string languageQuery = settings.Language is { } language
            ? "language=" + Uri.EscapeDataString(language) : "detect_language=true";
        string path = $"v1/listen?model={Uri.EscapeDataString(settings.Model!)}&{languageQuery}";
        using var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(PcmAudio.ToWave(pcm))
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        message.Headers.Authorization = new AuthenticationHeaderValue("Token", settings.ApiKey);
        using var response = await client.SendAsync(message, settings.TimeoutSeconds, cancellationToken);
        var root = response.RootElement;
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Object ||
            !results.TryGetProperty("channels", out var channels) || channels.ValueKind != JsonValueKind.Array || channels.GetArrayLength() != 1 ||
            channels[0].ValueKind != JsonValueKind.Object ||
            !channels[0].TryGetProperty("alternatives", out var alternatives) || alternatives.ValueKind != JsonValueKind.Array ||
            alternatives.GetArrayLength() == 0 || alternatives[0].ValueKind != JsonValueKind.Object ||
            !alternatives[0].TryGetProperty("transcript", out var text) || text.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(text.GetString()))
            throw new InvalidDataException("Deepgram returned an empty or malformed transcript.");
        return text.GetString()!.Trim();
    }
}
