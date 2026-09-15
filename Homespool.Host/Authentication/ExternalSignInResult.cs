namespace Homespool.Host.Authentication;

/// <summary>
/// What became of a provider's ticket when <see cref="ExternalSignIn.SignInAsync"/> tried to turn it
/// into a session, for the page to route on.
/// </summary>
public enum ExternalSignInResult
{
    /// <summary>
    /// Nobody set this. Never produced: reserved so that a value somebody forgot to assign cannot read
    /// as a provider identity with no account, and send the page on to the invite gate.
    /// </summary>
    Undefined = 0,

    /// <summary>No account has this provider identity linked; the page decides whether one may be created.</summary>
    NoAccount = 1,

    /// <summary>
    /// An account has it, and may not sign in - unconfirmed, typically. Never the password lockout,
    /// which a provider's answer does not consult; <c>LocalSignInRules.PreSignInCheckAsync</c> says why.
    /// </summary>
    NotAllowed = 2,

    /// <summary>An account has it and still owes its second factor; the pending cookie is written.</summary>
    RequiresSecondFactor = 4,

    /// <summary>The account is signed in.</summary>
    Succeeded = 5,
}
