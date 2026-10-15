namespace CameraSpeechMatch;

public sealed class CameraSpeechMatchResult
{
    /// <summary>
    /// Whether recording processing completed successfully.
    /// Acceptance is reported by <see cref="IsAccepted"/> and does not determine processing success.
    /// </summary>
    public bool Succeeded { get; set; }

    /// <summary>
    /// Whether processing succeeded and all minimum similarities required by the selected mode were met.
    /// False for completed mismatches and processing failures.
    /// </summary>
    public bool IsAccepted { get; set; }

    public CameraSpeechMatchMode MatchMode { get; set; } = CameraSpeechMatchMode.Lexical;

    /// <summary>Normalized wording similarity from 0 to 100, independent of semantic acceptance.</summary>
    public double LexicalSimilarityPercent { get; set; }

    /// <summary>The minimum lexical similarity captured for a Lexical or Combined attempt; null in Semantic mode.</summary>
    public double? MinimumLexicalSimilarityPercent { get; set; }

    /// <summary>Semantic similarity from 0 to 100; null when not evaluated, including a Combined lexical mismatch.</summary>
    public double? SemanticSimilarityPercent { get; set; }

    /// <summary>The minimum semantic similarity captured for a Semantic or Combined attempt; null in Lexical mode.</summary>
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
    /// Captured automatically and available when processing succeeds; otherwise null.
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
