using System;

using Microsoft.Extensions.Localization;

namespace Homespool.Host.Localisation;

/// <summary>
/// How long ago something happened, in a person's words and in their language.
/// </summary>
/// <remarks>
/// <para>
/// <b>The printer page's freshness line is the reason this exists.</b> A status card that refreshes
/// itself has to say how old what it shows is, or a page whose printer went away five minutes ago
/// looks exactly like one whose printer is answering - which is the failure a live view is supposed
/// to prevent, arriving dressed as the fix.
/// </para>
/// <para>
/// <b>Coarse on purpose.</b> It degrades a second at a time near now and an hour at a time further
/// out, because the question changes: seconds matter for "is this live", and past a minute or two
/// the only useful answer is roughly how stale it is.
/// </para>
/// </remarks>
public sealed class RelativeTimeText
{
    /// <summary>Below this, the exact number of seconds is noise and "just now" is the answer.</summary>
    private const int JustNowSeconds = 5;

    private readonly IStringLocalizer<SharedResource> _localiser;

    public RelativeTimeText(IStringLocalizer<SharedResource> localiser)
    {
        _localiser = localiser;
    }

    /// <summary>How long before <paramref name="now"/> the given instant was.</summary>
    /// <remarks>
    /// A future instant reads as "just now" rather than as a negative count. It happens for real: the
    /// printer stamps nothing, but a batch can be written a moment ahead of the clock the page reads,
    /// and "in -2 seconds" is a worse answer than a harmless rounding.
    /// </remarks>
    public string Since(DateTimeOffset at, DateTimeOffset now)
    {
        TimeSpan elapsed = now - at;

        if (elapsed.TotalSeconds < JustNowSeconds)
        {
            return _localiser["Common_JustNow"];
        }

        if (elapsed.TotalMinutes < 1)
        {
            return Plural.Format(_localiser, "Common_SecondsAgo", (int)elapsed.TotalSeconds);
        }

        if (elapsed.TotalHours < 1)
        {
            return Plural.Format(_localiser, "Common_MinutesAgo", (int)elapsed.TotalMinutes);
        }

        if (elapsed.TotalDays < 1)
        {
            return Plural.Format(_localiser, "Common_HoursAgo", (int)elapsed.TotalHours);
        }

        return Plural.Format(_localiser, "Common_DaysAgo", (int)elapsed.TotalDays);
    }
}
