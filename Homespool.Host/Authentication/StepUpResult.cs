using System;

namespace Homespool.Host.Authentication;

/// <summary>What <see cref="StepUpGate"/> made of the proof presented.</summary>
/// <param name="Refusal">Why the account was not proved, or <see cref="StepUpRefusal.None"/>.</param>
/// <param name="RetryAfter">
/// How long the refusal lasts, when it is one that ends by itself. It comes from the scheme rather
/// than from the account: a wrong step-up backs off its own counter and leaves the account lockout
/// alone, so asking the account how long it is locked out for would answer zero.
/// </param>
public readonly record struct StepUpResult(StepUpRefusal Refusal, TimeSpan? RetryAfter = null)
{
    /// <summary>A proved account.</summary>
    public static StepUpResult Proved => new(StepUpRefusal.None);

    /// <summary>Whether the account was proved.</summary>
    public bool Succeeded => Refusal is StepUpRefusal.None;

    /// <summary>An unproved account, for <paramref name="refusal"/>.</summary>
    public static StepUpResult Refused(StepUpRefusal refusal, TimeSpan? retryAfter = null)
    {
        return new StepUpResult(refusal, retryAfter);
    }
}
