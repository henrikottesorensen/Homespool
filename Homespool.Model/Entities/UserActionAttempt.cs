using System;

namespace Homespool.Model.Entities;

/// <summary>
/// One account's failed-attempt count and backoff for one <see cref="LimitedAction"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A table rather than a pair of columns on <c>HSUser</c>, which is where this started.</b> The
/// claim page carried <c>FailedClaimAttempts</c> and <c>ClaimLockoutEnd</c> directly on the user,
/// and a second action wanting the same treatment would have meant a second pair beside them - then
/// a third. Keyed on the action instead, a new one costs an enum member and nothing else.
/// </para>
/// <para>
/// <b>A row exists only once an account has failed something.</b> The common case is no row at all,
/// so the table stays roughly empty rather than growing a pair of zeroes for every account that
/// never gets anything wrong - and clearing a count deletes the row rather than zeroing it, so it
/// returns to that state.
/// </para>
/// </remarks>
public class UserActionAttempt
{
    public long Id { get; set; }

    /// <summary>The account being bounded. Cascades, since a counter outliving its account is noise.</summary>
    public long UserId { get; set; }

    /// <summary>
    /// Which action these attempts were against. Stored as text, like every other enum column here:
    /// legible in a raw SQLite session, and the enum's declaration order stops being part of the
    /// schema.
    /// </summary>
    public LimitedAction Action { get; set; }

    /// <summary>
    /// Consecutive failures. Reset to nothing by a success, not decayed - the backoff is what
    /// handles the passage of time.
    /// </summary>
    public int FailedCount { get; set; }

    /// <summary>
    /// When the account may try this action again, or null if it may now. Always in the future while
    /// it is doing anything; a past value simply means the backoff has elapsed.
    /// </summary>
    public DateTimeOffset? LockoutEnd { get; set; }
}
