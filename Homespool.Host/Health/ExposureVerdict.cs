namespace Homespool.Host.Health;

/// <summary>The verdict, and the sentence an administrator reads.</summary>
/// <param name="State">What is wrong.</param>
/// <param name="Description">A full sentence, shown verbatim.</param>
public sealed record ExposureVerdict(ExposureState State, string Description)
{
    /// <summary>Whether this is worth telling somebody about.</summary>
    public bool IsProblem => State != ExposureState.Ok;
}
