namespace CameraSpeechMatch;

public sealed class SystemOneMatchProbabilityRequest
{
    public required string ExpectedText { get; init; }

    public required string Transcript { get; init; }

    public required string Task { get; init; }

    public required IReadOnlyList<string> Rules { get; init; }

    public required string TrueCriterion { get; init; }

    public required string FalseCriterion { get; init; }
}
