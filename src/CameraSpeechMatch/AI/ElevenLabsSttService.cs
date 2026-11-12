using System.Net.Http.Headers;
using System.Text.Json;

namespace CameraSpeechMatch.AI;

internal sealed class ElevenLabsSttService(AiHttpClient client, CameraSpeechMatchTranscriptionOptions settings) : ISttService
{
    public bool IsAvailable => true;

    public async Task<string> TranscribeAsync(TranscribeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] pcm = PcmAudio.Validate(request, settings.MaximumAudioBytes);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(PcmAudio.ToWave(pcm));
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "recording.wav");
        form.Add(new StringContent(settings.Model!), "model_id");
        if (settings.Language is { } language) form.Add(new StringContent(language), "language_code");
        form.Add(new StringContent("false"), "tag_audio_events");
        form.Add(new StringContent("false"), "diarize");
        form.Add(new StringContent("false"), "webhook");
        form.Add(new StringContent("none"), "timestamps_granularity");
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/speech-to-text") { Content = form };
        message.Headers.Add("xi-api-key", settings.ApiKey);
        using var response = await client.SendAsync(message, settings.TimeoutSeconds, cancellationToken);
        if (!response.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(text.GetString()))
            throw new InvalidDataException("ElevenLabs returned an empty or malformed transcript.");
        return text.GetString()!.Trim();
    }
}
