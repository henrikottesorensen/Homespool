namespace Homespool.Host.Authentication;

/// <summary>What <see cref="StepUpGate"/> made of the proof presented.</summary>
/// <param name="Refusal">Why the account was not proved, or <see cref="StepUpRefusal.None"/>.</param>
public readonly record struct StepUpResult(StepUpRefusal Refusal)
{
    /// <summary>A proved account.</summary>
    public static StepUpResult Proved => new(StepUpRefusal.None);

    /// <summary>Whether the account was proved.</summary>
    public bool Succeeded => Refusal is StepUpRefusal.None;

    /// <summary>An unproved account, for <paramref name="refusal"/>.</summary>
    public static StepUpResult Refused(StepUpRefusal refusal)
    {
        return new StepUpResult(refusal);
    }
}
