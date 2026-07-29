using System;

using Microsoft.AspNetCore.Identity;

namespace Homespool.Model.Entities;

public class HSUser : IdentityUser<long>
{
    /// <summary>
    /// The maximum length of a <see cref="DisplayName"/>. Long enough for a real name, short enough
    /// that a header greeting cannot be used to deface a page.
    /// </summary>
    public const int DisplayNameMaxLength = 64;

    public HSUser()
    {
        SecurityStamp = Guid.NewGuid().ToString();
    }

    /// <summary>
    /// What the interface calls this person, as opposed to how they sign in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>UserName</c> remains the email address and remains the sign-in identifier - that is
    /// deliberate and unchanged. This exists only so the UI stops greeting people by their email
    /// address, and so <c>UserReadDTO.Name</c> has something better to return.
    /// </para>
    /// <para>
    /// Seeded at account creation from the email's local part, so it is useful before anyone edits
    /// it, and nullable because accounts predating it have none. Read
    /// <c>DisplayName ?? UserName ?? Email</c> wherever it is shown.
    /// </para>
    /// </remarks>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Consecutive registration codes this account has submitted that matched no pending
    /// registration. Reset to zero by a successful claim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shortening the claim code to ten characters is safe on the anonymous path because
    /// <c>/p/register</c> is rate limited; the claim page has no limiter at all, and this is what
    /// bounds guessing there. Persisted rather than held in memory because a restart would otherwise
    /// hand an attacker a fresh budget.
    /// </para>
    /// <para>
    /// <b>Only a code matching nothing counts.</b> An already-claimed code and a team the user may
    /// not claim into both mean the code was <i>right</i>, so neither is a guess.
    /// </para>
    /// <para>
    /// <b>Keyed on the user, deliberately, not on the registration.</b> A wrong code finds no
    /// registration row, so there is nothing to count against - per-registration counting needs a
    /// second identifier submitted alongside the code, which only a pending-printer list would
    /// supply. Keying on the account is also what makes the enrollment-DoS hazard unreachable: an
    /// attacker can only burn their own budget, never a victim's pending registration.
    /// </para>
    /// </remarks>
    public int FailedClaimAttempts { get; set; }

    /// <summary>
    /// When this account may attempt another claim, or null if it is not currently backed off.
    /// </summary>
    /// <remarks>
    /// Backoff rather than invalidation, so a wrong guess never destroys a pending registration -
    /// and the person is standing at the printer, where a fresh code is one menu press away.
    /// </remarks>
    public DateTimeOffset? ClaimLockoutEnd { get; set; }

    /// <summary>
    /// What a new account's <see cref="DisplayName"/> starts as: the email's local part.
    /// </summary>
    /// <remarks>
    /// Seeded rather than left null so the interface stops showing addresses immediately, instead of
    /// only for people who go and set one. Returns null for an address with no local part, which
    /// leaves the fallback chain to do its job rather than storing an empty string.
    /// </remarks>
    public static string? DefaultDisplayNameFor(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        int at = email.IndexOf('@');
        string local = at < 0 ? email : email[..at];

        if (string.IsNullOrWhiteSpace(local))
        {
            return null;
        }

        return local.Length > DisplayNameMaxLength ? local[..DisplayNameMaxLength] : local;
    }

    public HSUser(string userName)
        : this()
    {
        UserName = userName;
    }
}
