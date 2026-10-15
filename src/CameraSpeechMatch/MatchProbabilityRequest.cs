namespace CameraSpeechMatch;

/// <summary>A single System One noul decision, returning the probability of true.</summary>
public sealed class MatchProbabilityRequest
{
    /// <summary>The data to judge: a nonblank string or a JSON-serializable object or array.</summary>
    public required object State { get; init; }

    /// <summary>The question or guidance: a nonblank string or a JSON-serializable object or array.</summary>
    public required object Instructions { get; init; }

    /// <summary>Optional description of the true outcome. Null uses the model's default.</summary>
    public string? TrueCriteria { get; init; }

    /// <summary>Optional description of the false outcome. Null uses the model's default.</summary>
    public string? FalseCriteria { get; init; }
}
