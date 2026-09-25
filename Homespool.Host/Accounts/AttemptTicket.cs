using System;

namespace Homespool.Host.Accounts;

/// <summary>
/// One attempt at a guessable secret, counted as a failure <i>before</i> the secret is compared - or
/// the refusal to count one, because the caller is already backed off.
/// </summary>
/// <remarks>
/// <para>
/// <b>The count comes first so that the check is also the reservation.</b> Checking a backoff, then
/// comparing, then recording lets every request of a burst pass the check before any of them has
/// written, and each is compared. Taking the attempt with a write that lands only on the row the check
/// read means the request that crosses the threshold imposes the backoff on the rest of the burst
/// while it is still being compared, so a burst gets exactly what a patient caller gets.
/// </para>
/// <para>
/// <b>A right answer gives the attempt back</b>, and lifts the backoff it imposed, when there is one:
/// <see cref="LockoutImposed"/> is how the giving back knows that the backoff is its own and not a
/// later caller's. A request that dies between taking and answering keeps the attempt counted, which
/// is the failure to prefer.
/// </para>
/// </remarks>
public sealed class AttemptTicket
{
    private AttemptTicket(TimeSpan? backedOff, DateTimeOffset? lockoutImposed)
    {
        BackedOff = backedOff;
        LockoutImposed = lockoutImposed;
    }

    /// <summary>
    /// How much longer the caller is backed off when the attempt was refused, and nothing was counted;
    /// <see langword="null"/> when it was taken.
    /// </summary>
    public TimeSpan? BackedOff { get; }

    /// <summary>
    /// The backoff end this attempt set by crossing the threshold, or <see langword="null"/> when it
    /// set none. A wrong answer leaves it in force; a right one lifts it.
    /// </summary>
    public DateTimeOffset? LockoutImposed { get; }

    /// <summary>Whether the attempt was counted and the secret may be compared.</summary>
    public bool Taken => BackedOff is null;

    /// <summary>An attempt counted, which set <paramref name="lockoutImposed"/> if it crossed the threshold.</summary>
    public static AttemptTicket Counted(DateTimeOffset? lockoutImposed)
    {
        return new AttemptTicket(null, lockoutImposed);
    }

    /// <summary>An attempt refused, uncounted, for a caller backed off for <paramref name="remaining"/> more.</summary>
    public static AttemptTicket Refused(TimeSpan remaining)
    {
        return new AttemptTicket(remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining, null);
    }
}
