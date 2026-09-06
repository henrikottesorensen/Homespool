namespace Homespool.Host.Authentication;

/// <summary>
/// What a page collected for the <see cref="Schemes.Totp"/> scheme on a step-up: the signed-in
/// account's authenticator code, typed again before something the session alone should not be
/// allowed to do. Verified against the session's account and no other - a browser that also holds a
/// pending sign-in for some other account does not get that account's code accepted here. A wrong
/// one backs off the account's step-ups, never its sign-in.
/// </summary>
/// <param name="Code">The code, as typed.</param>
public sealed record TotpStepUpCredential(string? Code);
