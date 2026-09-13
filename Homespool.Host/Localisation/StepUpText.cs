using System;

using Microsoft.Extensions.Localization;

using Homespool.Host.Authentication;
using Homespool.Host.Pages.Printers;

namespace Homespool.Host.Localisation;

/// <summary>
/// What a refused step-up is told to the person who was refused.
/// </summary>
/// <remarks>
/// <b>One place for the proof page's refusals.</b> A wrong password says so plainly; a lockout carries
/// the wait, which is the only part of it a reader can act on; a provider's answer that did not count
/// says why. The sentences say nothing about which act was refused - the page the reader was sent
/// from has already said that.
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
            _ => throw new ArgumentException("A proved step-up has nothing to say.", nameof(result)),
        };
    }

    /// <summary>
    /// What to tell the reader who has just come back from their provider and was not confirmed: why
    /// the round trip did not count.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="outcome"/> confirmed the account.</exception>
    public string Describe(ProviderProofOutcome outcome)
    {
        string provider = outcome.Provider ?? _localiser["StepUp_YourProvider"].Value;

        return outcome.Refusal switch
        {
            null => throw new ArgumentException("A confirmed provider answer has nothing to say.", nameof(outcome)),
            "mismatch" => _localiser["StepUp_ProviderMismatch", provider].Value,
            "stale" => _localiser["StepUp_ProviderStale", provider].Value,
            _ => _localiser["StepUp_ProviderFailed"].Value,
        };
    }
}
