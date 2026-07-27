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
