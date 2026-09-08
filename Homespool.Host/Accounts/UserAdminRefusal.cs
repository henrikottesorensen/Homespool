namespace Homespool.Host.Accounts;

/// <summary>Why <see cref="UserAdministration"/> declined to act on an account.</summary>
public enum UserAdminRefusal
{
    /// <summary>It acted.</summary>
    None = 0,

    /// <summary>No account carries that id. The page answers this as a 404 rather than a message.</summary>
    NoSuchAccount = 1,

    /// <summary>
    /// The administrator aimed at their own account. Refused for deactivation only, and not out of
    /// paternalism: an administrator who closes their own account is the one person who cannot then
    /// reopen it.
    /// </summary>
    Self = 2,

    /// <summary>
    /// The subject is the only administrator still active. Deactivating them would leave the
    /// deployment with nobody who can administer it and no way back - first-time setup stays closed
    /// while an administrator account exists, deactivated or not - so the repair would be editing the
    /// database.
    /// </summary>
    LastAdministrator = 3,
}
