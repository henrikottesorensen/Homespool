namespace Homespool.Host.Authentication;

/// <summary>
/// Why a step-up did not prove the account, or that it did.
/// </summary>
/// <remarks>
/// Three refusals rather than one because the pages answer them differently: a wrong password is
/// worth saying plainly, a lockout carries a wait the reader needs, and a missing provider proof is
/// an instruction rather than a failure - go and confirm at the provider first.
/// </remarks>
public enum StepUpRefusal
{
    /// <summary>The account was proved.</summary>
    None = 0,

    /// <summary>The password was wrong, empty, or belonged to nobody signed in.</summary>
    WrongPassword,

    /// <summary>The account is locked out, so no password is compared until it is not.</summary>
    LockedOut,

    /// <summary>No provider proof was presented, or the one presented was not this account's.</summary>
    NoProviderProof,
}
