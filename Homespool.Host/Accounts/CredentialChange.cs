namespace Homespool.Host.Accounts;

/// <summary>A change to the ways an account can be signed into, which its owner is mailed about.</summary>
public enum CredentialChange
{
    /// <summary>
    /// Nobody set this. Never sent: reserved so that a value somebody forgot to assign cannot pass for
    /// a change that happened.
    /// </summary>
    Undefined = 0,

    /// <summary>An external provider was linked, and is now a sign-in beside whatever else the account holds.</summary>
    ProviderLinked = 1,

    /// <summary>An external provider was unlinked, and the account still has another way in.</summary>
    ProviderRemoved = 2,

    /// <summary>
    /// The account's last external provider was unlinked and a password set in its place. Worded apart
    /// from <see cref="ProviderRemoved"/> because the owner can no longer sign in the way they did.
    /// </summary>
    ProviderSwappedForPassword = 3,

    /// <summary>The owner's own page added a passkey.</summary>
    PasskeyAdded = 4,

    /// <summary>The owner's own page removed a passkey.</summary>
    PasskeyRemoved = 5,

    /// <summary>An administrator revoked one of the account's passkeys.</summary>
    PasskeyRevoked = 6,
}
