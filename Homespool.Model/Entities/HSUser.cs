using System;
using System.ComponentModel.DataAnnotations;

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
    /// The maximum length of <see cref="Language"/>. Long enough for the longest culture name that
    /// could reasonably be offered (<c>zh-Hant-TW</c>), short enough to be obviously not free text.
    /// </summary>
    public const int LanguageMaxLength = 16;

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

    /// <summary>
    /// The language this account reads Homespool in, as a culture name (<c>en</c>, <c>da</c>), or
    /// null to follow whatever the browser asks for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Null is not "English".</b> It means nobody has chosen, so the browser decides and the
    /// choice follows a person who changes their system language. A user who picked English gets
    /// <c>en</c> stored and stops following the browser, which is a different thing and has to stay
    /// distinguishable - the same argument <see cref="Printer.Name"/> and <c>Camera.Name</c> both
    /// make for staying null until somebody types something.
    /// </para>
    /// <para>
    /// <b>Persisted rather than left to the request, because the request is not always there.</b>
    /// Alerts and invitations are sent from hosted services with no <c>Accept-Language</c> to read -
    /// see <c>TelemetryAlertService</c> - so a preference that lived only in a cookie would mean
    /// every email fell back to the server's culture. That is the whole reason this column exists
    /// ahead of any string being translated.
    /// </para>
    /// <para>
    /// A culture name rather than an enum: the set of shipped languages is configuration, and an
    /// enum would put it in the schema, where adding one is a migration.
    /// </para>
    /// </remarks>
    [MaxLength(LanguageMaxLength)]
    public string? Language { get; set; }

    public HSUser(string userName)
        : this()
    {
        UserName = userName;
    }
}
