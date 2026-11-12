namespace CameraSpeechMatch;

/// <summary>
/// Recognizes speech in a recording. Built-in providers implement this interface; hosts may supply an optional override.
/// </summary>
public interface ISttService
{
    /// <summary>Whether the host can transcribe speech.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Transcribes the spoken audio in <paramref name="request"/>.
    /// Returns the recognized text from the configured speech-to-text provider.
    /// The implementation selects the transcription language or uses its provider's language detection.
    /// </summary>
    /// <param name="request">Recorded PCM audio.</param>
    /// <param name="cancellationToken">Cancels transcription.</param>
    /// <returns>The recognized transcript. Return an empty string when no speech was recognized.</returns>
    Task<string> TranscribeAsync(TranscribeRequest request, CancellationToken cancellationToken);
}
