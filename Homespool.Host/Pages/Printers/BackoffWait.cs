using System;

using Microsoft.Extensions.Localization;

using Homespool.Host.Localisation;

namespace Homespool.Host.Pages.Printers;

/// <summary>
/// Renders a limiter's backoff as something worth reading on a form — "45 seconds", "3 minutes" —
/// rather than a raw <see cref="TimeSpan"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared rather than copied</b>, which is the same argument the claim page already made about
/// the resource keys underneath it: two copies of one sentence drift apart in translation, and a
/// formatter is a sentence with arithmetic attached. It was private to <c>ClaimModel</c> until the
/// printer-removal confirmation needed the identical thing, 2026-08-26.
/// </para>
/// <para>
/// Static and localiser-taking, following <see cref="PrintDuration"/>: nothing here has state, and a
/// service registration would be ceremony around one method.
/// </para>
/// </remarks>
public static class BackoffWait
{
    /// <summary>
    /// Spells <paramref name="remaining"/> for a person who has just been refused.
    /// </summary>
    /// <remarks>
    /// <b>Rounds up</b>, so the message never tells somebody to retry a moment before they may.
    /// </remarks>
    public static string Format(IStringLocalizer localiser, TimeSpan remaining)
    {
        ArgumentNullException.ThrowIfNull(localiser);

        if (remaining < TimeSpan.FromMinutes(1))
        {
            int seconds = (int)Math.Ceiling(remaining.TotalSeconds);

            return seconds == 1
                ? localiser["Common_WaitOneSecond"].Value
                : localiser["Common_WaitSeconds", seconds].Value;
        }

        int minutes = (int)Math.Ceiling(remaining.TotalMinutes);

        return Plural.Format(localiser, "Common_Minutes", minutes);
    }
}
