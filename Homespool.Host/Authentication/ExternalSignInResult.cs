namespace Homespool.Host.Authentication;

/// <summary>
/// What became of a provider's ticket when <see cref="ExternalSignIn.SignInAsync"/> tried to turn it
/// into a session, for the page to route on.
/// </summary>
public enum ExternalSignInResult
{
    /// <summary>No account has this provider identity linked; the page decides whether one may be created.</summary>
    NoAccount = 0,

    /// <summary>An account has it, and may not sign in - unconfirmed, typically.</summary>
    NotAllowed = 1,

    /// <summary>An account has it, and is locked out.</summary>
    LockedOut = 2,

    /// <summary>An account has it and still owes its second factor; the pending cookie is written.</summary>
    RequiresSecondFactor = 3,

    /// <summary>The account is signed in.</summary>
    Succeeded = 4,
}
