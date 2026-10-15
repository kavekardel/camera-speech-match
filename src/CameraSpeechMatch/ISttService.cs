namespace CameraSpeechMatch;

/// <summary>
/// Recognizes speech in a recording. The host application implements this interface.
/// </summary>
public interface ISttService
{
    /// <summary>Whether the host can transcribe speech.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Transcribes the spoken audio in <paramref name="request"/>.
    /// Implement this method in the host application with its speech-to-text provider and return the recognized text.
    /// The implementation selects the transcription language or uses its provider's language detection.
    /// </summary>
    /// <param name="request">Recorded PCM audio.</param>
    /// <param name="cancellationToken">Cancels transcription.</param>
    /// <returns>The recognized transcript. Return an empty string when no speech was recognized.</returns>
    Task<string> TranscribeAsync(TranscribeRequest request, CancellationToken cancellationToken);
}
