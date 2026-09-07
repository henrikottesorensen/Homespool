using System;

using Microsoft.Extensions.Localization;

using Homespool.Host.Authentication;
using Homespool.Host.Pages.Printers;

namespace Homespool.Host.Localisation;

/// <summary>
/// What a refused step-up is told to the person who was refused.
/// </summary>
/// <remarks>
/// <b>One place, because four pages refuse for the same three reasons.</b> A wrong password says so
/// plainly; a lockout carries the wait, which is the only part of it a reader can act on; a missing
/// provider proof is an instruction rather than a failure, since the round trip is still there to be
/// taken. The sentences say nothing about which act was refused - the page the reader is looking at
/// has already said that.
/// </remarks>
public class StepUpText
{
    private readonly IStringLocalizer<SharedResource> _localiser;

    public StepUpText(IStringLocalizer<SharedResource> localiser)
    {
        _localiser = localiser;
    }

    /// <summary>
    /// The sentence for <paramref name="result"/>, which must be a refusal.
    /// </summary>
    /// <remarks>
    /// <b>The wait comes off the refusal, not out of the account.</b> A wrong step-up backs off its
    /// own counter and leaves the account lockout untouched, so asking the account how long it is
    /// locked out for answers zero and the reader is told to try again in no time at all.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="result"/> proved the account.</exception>
    public string Describe(StepUpResult result)
    {
        return result.Refusal switch
        {
            StepUpRefusal.LockedOut =>
                _localiser["StepUp_LockedOut", BackoffWait.Format(_localiser, result.RetryAfter ?? TimeSpan.Zero)],
            StepUpRefusal.WrongPassword => _localiser["StepUp_PasswordWrong"],
            StepUpRefusal.NoProviderProof => _localiser["StepUp_ProviderNotConfirmed"],
            _ => throw new ArgumentException("A proved step-up has nothing to say.", nameof(result)),
        };
    }

    /// <summary>
    /// What to tell the reader who has just come back from their provider - that they are confirmed
    /// and for how long, or why the round trip did not count.
    /// </summary>
    public string Describe(ProviderProofOutcome outcome)
    {
        string provider = outcome.Provider ?? _localiser["StepUp_YourProvider"].Value;

        return outcome.Refusal switch
        {
            null => _localiser["StepUp_ProviderConfirmed", provider].Value,
            "mismatch" => _localiser["StepUp_ProviderMismatch", provider].Value,
            "stale" => _localiser["StepUp_ProviderStale", provider].Value,
            _ => _localiser["StepUp_ProviderFailed"].Value,
        };
    }
}
