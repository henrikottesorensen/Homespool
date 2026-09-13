namespace Homespool.Host.Authentication;

/// <summary>
/// Why a step-up did not prove the account, or that it did.
/// </summary>
/// <remarks>
/// Two refusals rather than one because the page answers them differently: a wrong password is worth
/// saying plainly, and a lockout carries a wait the reader needs.
/// </remarks>
public enum StepUpRefusal
{
    /// <summary>The account was proved.</summary>
    None = 0,

    /// <summary>The password was wrong, empty, or belonged to nobody signed in.</summary>
    WrongPassword,

    /// <summary>The account is locked out, so no password is compared until it is not.</summary>
    LockedOut,
}
