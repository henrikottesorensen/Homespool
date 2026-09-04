using System;

using Squint;

namespace Homespool.Host.Accounts;

/// <summary>
/// What is done to a typed username before it is handed to Identity: an acceptable name is stored
/// in its clean form, and an unacceptable one is stored as typed so that
/// <see cref="UsernameValidator"/> can say what is wrong with it.
/// </summary>
/// <remarks>
/// <para>
/// Applied at every place a username enters - setup, registration, the external-login account
/// door and the rename on <c>Account/Manage</c> - because Identity's validators may refuse a value
/// but may not rewrite one. The clean form is Squint's <see cref="Inspection.CleanForm"/>: the same
/// letters, with a decomposed accent composed. It differs from the input only for a name that is
/// acceptable in the first place, which is the point: a ligature or a fullwidth letter is a finding
/// against the name, not something to fold away before anyone looks. A person who typed one pasted
/// it or is up to something, and the validator tells them so.
/// </para>
/// <para>
/// <see cref="UsernameValidator"/> refuses a name that is acceptable but not in its clean form, so a
/// new entry point that forgets this call fails loudly on its first accented name rather than storing
/// a second spelling of an existing one.
/// </para>
/// </remarks>
public static class Usernames
{
    /// <summary>
    /// The form of <paramref name="username"/> to hand to Identity: the clean form when the name is
    /// acceptable, the input itself when it is not.
    /// </summary>
    public static string Prepare(string username)
    {
        ArgumentNullException.ThrowIfNull(username);

        Inspection inspection = Names.Inspect(username, NamePolicy.OneScript);

        return inspection.IsAcceptable ? inspection.CleanForm : username;
    }
}
