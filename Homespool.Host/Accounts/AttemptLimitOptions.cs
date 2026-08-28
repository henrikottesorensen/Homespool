using System.ComponentModel.DataAnnotations;

namespace Homespool.Host.Accounts;

/// <summary>
/// How hard an account may guess at a short secret before it is backed off.
/// </summary>
/// <remarks>
/// <para>
/// <b>One set of knobs for every <see cref="Homespool.Model.LimitedAction"/>, deliberately.</b> The
/// numbers came from the claim page and are unchanged; a second action wanting different ones can
/// have a per-action override added then, rather than three more properties now for a distinction
/// nobody has asked for. The defaults were <c>PrusaConnectOptions</c>' until 2026-08-26.
/// </para>
/// <para>
/// <b>Only <see cref="MaxFailedAttempts"/> is offered on the settings page, and the two timings are
/// deliberately not</b> (Henrik) - kept here so the question is not reopened. They do not mean what
/// two fields would suggest: they are not "first wait" and "longest wait" set independently, but
/// together decide where the doubling saturates, which is not visible from either number. 30 and
/// 3600 saturate on the eighth failure past the threshold; a base of 5 moves that to the tenth and
/// leaves the whole early curve toothless. The dangerous direction is also the silent one, since a
/// base of 1 leaves the limiter counting and logging while bounding nothing at all. How many fumbles
/// somebody tolerates is a preference; the shape of the backoff is a security decision, and neither
/// was ever settable in <c>.env</c>, so putting them on a page would invent a surface rather than
/// rehouse one. Both remain bindable for anybody editing the settings file directly.
/// </para>
/// </remarks>
public class AttemptLimitOptions
{
    public const string SectionName = "AttemptLimits";

    /// <summary>Failures tolerated before any backoff is applied.</summary>
    [Range(0, 10_000)]
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>
    /// The first backoff applied once <see cref="MaxFailedAttempts"/> is passed. Doubles per further
    /// failure, up to <see cref="LockoutMaxSeconds"/>.
    /// </summary>
    // Not zero: a base of zero leaves every doubled backoff at zero, so the limiter would go on
    // counting failures while bounding nothing, and look configured while doing it.
    [Range(1, 86_400)]
    public int LockoutBaseSeconds { get; set; } = 30;

    /// <summary>
    /// The ceiling on a doubled backoff. A cap rather than an ever-growing wait: the point is to
    /// make grinding uneconomic, not to lock somebody out of their own printers for a day.
    /// </summary>
    [Range(1, 604_800)]
    public int LockoutMaxSeconds { get; set; } = 3600;
}
