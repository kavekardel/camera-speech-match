namespace CameraSpeechMatch;

/// <summary>
/// Evaluates a System One noul question about the supplied state.
/// The host application implements this interface.
/// </summary>
public interface ISystemOneService
{
    bool IsAvailable { get; }

    /// <summary>Returns the probability of true, from 0 to 1, for the request's instructions and criteria.</summary>
    Task<double> MatchProbabilityAsync(MatchProbabilityRequest request, CancellationToken cancellationToken);
}
