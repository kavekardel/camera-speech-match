namespace CameraSpeechMatch;

/// <summary>A single semantic decision, returning a match estimate from 0 to 1.</summary>
public sealed class MatchProbabilityRequest
{
    /// <summary>The data to judge: a nonblank string or a JSON-serializable object or array.</summary>
    public required object State { get; init; }

    /// <summary>The question or guidance: a nonblank string or a JSON-serializable object or array.</summary>
    public required object Instructions { get; init; }

    /// <summary>Optional description of the true outcome. Null leaves this outcome's guidance unspecified.</summary>
    public string? TrueCriteria { get; init; }

    /// <summary>Optional description of the false outcome. Null leaves this outcome's guidance unspecified.</summary>
    public string? FalseCriteria { get; init; }
}
