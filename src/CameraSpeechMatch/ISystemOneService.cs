namespace CameraSpeechMatch;

/// <summary>
/// Decides whether a transcript has the same meaning as the expected prompt.
/// The host application implements this interface.
/// </summary>
public interface ISystemOneService
{
    bool IsAvailable { get; }

    /// <summary>Returns a probability from 0 to 1 that the transcript matches the prompt.</summary>
    Task<double> MatchProbabilityAsync(SystemOneMatchProbabilityRequest request, CancellationToken cancellationToken);
}
