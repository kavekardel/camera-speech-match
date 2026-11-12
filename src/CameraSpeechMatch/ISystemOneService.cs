namespace CameraSpeechMatch;

/// <summary>
/// Evaluates a semantic decision about the supplied state.
/// Built-in providers implement this interface; hosts may supply an optional override.
/// </summary>
public interface ISystemOneService
{
    bool IsAvailable { get; }

    /// <summary>Returns a finite match estimate from 0 to 1. System One probabilities and chat-generated scores require separate threshold evaluation.</summary>
    Task<double> MatchProbabilityAsync(MatchProbabilityRequest request, CancellationToken cancellationToken);
}
