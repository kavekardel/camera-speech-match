namespace CameraSpeechMatch;

/// <summary>Audio passed to <see cref="ISttService.TranscribeAsync"/>.</summary>
public sealed class SttTranscribeRequest
{
    /// <summary>Language of the speech. The component sends <c>fa</c> or <c>en</c>.</summary>
    public required string Language { get; init; }

    /// <summary>
    /// Optional raw mono, 16 kHz, signed 16-bit little-endian PCM audio captured alongside the video.
    /// Populated when the component's CapturePcmAudio option is enabled; limited to 10 MiB.
    /// </summary>
    public byte[]? PcmAudioContent { get; init; }
}
