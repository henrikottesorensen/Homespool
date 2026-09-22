namespace Homespool.Host.Accounts;

/// <summary>Why <see cref="UserAdministration"/> declined to act on an account.</summary>
public enum UserAdminRefusal
{
    /// <summary>
    /// Nobody set this. Never produced: reserved so that a value somebody forgot to assign cannot read
    /// as an act that went through.
    /// </summary>
    Undefined = 0,

    /// <summary>It acted.</summary>
    None = 1,

    /// <summary>No account carries that id. The page answers this as a 404 rather than a message.</summary>
    NoSuchAccount = 2,

    /// <summary>
    /// The administrator aimed at their own account. Refused for deactivation, and not out of
    /// paternalism: an administrator who closes their own account is the one person who cannot then
    /// reopen it - and this refusal, beside <see cref="NotAnAdministrator"/>, is what keeps at least
    /// one administrator open. Refused for a passkey revoke too, because their own passkeys have their
    /// own page.
    /// </summary>
    Self = 3,

    /// <summary>
    /// The account asking is not an open administrator: closed, never one, or no account at all. The
    /// service asks for itself, by <see cref="Administrators.Open"/>, rather than trusting its caller
    /// to have - a role claim is what a cookie was issued with, and a caller that is not the page
    /// may have checked nothing. The page answers this as a forbidden rather than a message.
    /// </summary>
    NotAnAdministrator = 5,
}
