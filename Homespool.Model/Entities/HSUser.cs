using System;

using Microsoft.AspNetCore.Identity;

namespace Homespool.Model.Entities;

public class HSUser : IdentityUser<long>
{
    /// <summary>
    /// The maximum length of a <c>UserName</c>. Long enough for a real name, short enough that a
    /// header greeting cannot be used to deface a page.
    /// </summary>
    public const int UsernameMaxLength = 64;

    /// <summary>
    /// Every character a <c>UserName</c> may contain: Identity's own default set, less <c>@</c> and
    /// <c>+</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>@</c> is excluded because sign-in accepts an email address or a username in one field</b>
    /// (<c>Account/Login</c>). Allowing an address-shaped username would let one account occupy
    /// another's address in the sign-in namespace, and the resolution order - username first - is what
    /// would decide who gets the password attempt. Excluding the character makes the two namespaces
    /// disjoint by construction rather than by lookup order. <c>+</c> goes with it: it only ever
    /// appeared here as part of an address.
    /// </para>
    /// <para>
    /// Applied by <c>IdentityOptions.User.AllowedUserNameCharacters</c>, so Identity's own
    /// <c>UserValidator</c> is the single place it is enforced - on creation and on every later change
    /// alike. Nothing re-implements the check at the page layer.
    /// </para>
    /// </remarks>
    public const string AllowedUsernameCharacters =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._";

    public HSUser()
    {
        SecurityStamp = Guid.NewGuid().ToString();
    }

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
    /// supply. Keying on the account is also what makes the enrolment-DoS hazard unreachable: an
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

    public HSUser(string userName)
        : this()
    {
        UserName = userName;
    }
}
