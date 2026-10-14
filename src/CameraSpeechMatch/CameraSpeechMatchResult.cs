namespace CameraSpeechMatch;

public sealed class CameraSpeechMatchResult
{
    /// <summary>
    /// Whether recording processing completed successfully.
    /// Match scores are reported separately and do not determine processing success.
    /// </summary>
    public bool Succeeded { get; set; }

    public CameraSpeechMatchMode MatchMode { get; set; } = CameraSpeechMatchMode.Lexical;

    /// <summary>Normalized wording similarity from 0 to 100, independent of semantic acceptance.</summary>
    public double LexicalSimilarityPercent { get; set; }

    /// <summary>The minimum similarity captured for this Lexical attempt; null in Semantic mode.</summary>
    public double? MinimumLexicalSimilarityPercent { get; set; }

    /// <summary>Semantic similarity from 0 to 100; null in Lexical mode.</summary>
    public double? SemanticSimilarityPercent { get; set; }

    /// <summary>The minimum semantic similarity percentage captured for this attempt; null in Lexical mode.</summary>
    public double? MinimumSemanticSimilarityPercent { get; set; }

    public string ExpectedText { get; set; } = string.Empty;

    public string TranscribedText { get; set; } = string.Empty;

    /// <summary>Recorded video bytes. Null when processing failed.</summary>
    public byte[]? VideoContent { get; set; }

    public string? VideoContentType { get; set; }

    public string? VideoFileName { get; set; }

    public long VideoContentLength { get; set; }

    /// <summary>
    /// Raw mono, 16 kHz, signed 16-bit little-endian PCM audio captured during video recording.
    /// Available when CapturePcmAudio is enabled and processing succeeds; otherwise null.
    /// </summary>
    public byte[]? PcmAudioContent { get; set; }

    /// <summary>PCM media type: application/octet-stream. Null when no audio metadata is available.</summary>
    public string? PcmAudioContentType { get; set; }

    /// <summary>PCM file name using the video's base name and a .pcm extension.</summary>
    public string? PcmAudioFileName { get; set; }

    /// <summary>Captured PCM byte count. Zero when no audio metadata is available.</summary>
    public long PcmAudioContentLength { get; set; }

    public string? ErrorMessage { get; set; }
}
