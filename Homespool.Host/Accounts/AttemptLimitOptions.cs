namespace Homespool.Host.Accounts;

/// <summary>
/// How hard an account may guess at a short secret before it is backed off.
/// </summary>
/// <remarks>
/// <b>One set of knobs for every <see cref="Homespool.Model.LimitedAction"/>, deliberately.</b> The
/// numbers came from the claim page and are unchanged; a second action wanting different ones can
/// have a per-action override added then, rather than three more properties now for a distinction
/// nobody has asked for. The defaults were <c>PrusaConnectOptions</c>' until 2026-08-26.
/// </remarks>
public class AttemptLimitOptions
{
    public const string SectionName = "AttemptLimits";

    /// <summary>Failures tolerated before any backoff is applied.</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>
    /// The first backoff applied once <see cref="MaxFailedAttempts"/> is passed. Doubles per further
    /// failure, up to <see cref="LockoutMaxSeconds"/>.
    /// </summary>
    public int LockoutBaseSeconds { get; set; } = 30;

    /// <summary>
    /// The ceiling on a doubled backoff. A cap rather than an ever-growing wait: the point is to
    /// make grinding uneconomic, not to lock somebody out of their own printers for a day.
    /// </summary>
    public int LockoutMaxSeconds { get; set; } = 3600;
}
