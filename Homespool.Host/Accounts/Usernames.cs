using System;

using Squint;

namespace Homespool.Host.Accounts;

/// <summary>
/// What is done to a username before it is stored: the NFKC form, so that one name has one
/// spelling in the database whatever the keyboard produced.
/// </summary>
/// <remarks>
/// <para>
/// Applied at every place a username enters - setup, registration, the external-login account
/// door and the rename on <c>Account/Manage</c> - because Identity's validators may refuse a value
/// but may not rewrite one. <see cref="UsernameValidator"/> refuses a name that is not already in
/// this form, so a new entry point that forgets this call fails loudly on its first non-ASCII name
/// rather than storing a second spelling of an existing one.
/// </para>
/// <para>
/// NFKC rather than NFC because UTS #39 asks for it: it also folds compatibility characters -
/// fullwidth letters, ligatures such as <c>ﬁ</c> - into the letters they are read as, which is
/// what a lookup key wants and a display form does not mind.
/// </para>
/// </remarks>
public static class Usernames
{
    /// <summary>The NFKC form of <paramref name="username"/>, computed from Squint's tables.</summary>
    public static string Normalise(string username)
    {
        ArgumentNullException.ThrowIfNull(username);

        return Normalization.Nfkc(username);
    }
}
