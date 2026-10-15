namespace CameraSpeechMatch;

/// <summary>Audio passed to <see cref="ISttService.TranscribeAsync"/>.</summary>
public sealed class SttTranscribeRequest
{
    /// <summary>
    /// Raw mono, 16 kHz, signed 16-bit little-endian PCM audio captured alongside the video.
    /// Automatically populated by the component; limited to 10 MiB.
    /// </summary>
    public byte[]? PcmAudioContent { get; init; }
}
