using Microsoft.Extensions.AI;

namespace CameraSpeechMatch.AI;

// Keep the experimental speech API behind the existing public ISttService contract.
#pragma warning disable MEAI001
internal sealed class OpenAISttService : ISttService, IDisposable
{
    private readonly OpenAIProvider provider;
    private readonly ISpeechToTextClient client;
    private readonly CameraSpeechMatchTranscriptionOptions settings;

    internal OpenAISttService(HttpClient http, Uri endpoint, CameraSpeechMatchTranscriptionOptions settings)
    {
        this.settings = settings;
        provider = new(http, endpoint, settings);
        client = provider.Client.GetAudioClient(settings.Model!).AsISpeechToTextClient();
    }

    public bool IsAvailable => true;

    public Task<string> TranscribeAsync(TranscribeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] audio = PcmAudio.Validate(request, settings.MaximumAudioBytes);
        return OpenAIProvider.InvokeAsync(async token =>
        {
            using var wave = new MemoryStream(PcmAudio.ToWave(audio), writable: false);
            var response = await client.GetTextAsync(wave, new SpeechToTextOptions { SpeechLanguage = settings.Language }, token);
            string text = response.Text.Trim();
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidDataException("Speech recognition returned an empty transcript.");
            return text;
        }, settings.TimeoutSeconds, cancellationToken);
    }

    public void Dispose()
    {
        client.Dispose();
        provider.Dispose();
    }
}
#pragma warning restore MEAI001
