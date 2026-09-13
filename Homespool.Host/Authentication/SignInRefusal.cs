namespace Homespool.Host.Authentication;

/// <summary>
/// Why a local credential scheme refused, when the page that asked needs to route on it: a locked-out
/// account goes to the lockout page, an account that may not sign in gets the same message as a
/// wrong password, and everything else is simply wrong.
/// </summary>
public enum SignInRefusal
{
    /// <summary>
    /// Nobody set this. Never carried by a refusal: <see cref="SignInRefusals.Fail(SignInRefusal, string)"/>
    /// throws on it, and <see cref="SignInRefusals.Refusal"/> reads it as <see cref="Invalid"/>.
    /// </summary>
    Undefined = 0,

    /// <summary>The credential was wrong, or absent where it was required.</summary>
    Invalid = 1,

    /// <summary>The account is locked out, before or as a result of this attempt.</summary>
    LockedOut = 2,

    /// <summary>The credential was right and the account may still not sign in - unconfirmed, typically.</summary>
    NotAllowed = 3,
}
