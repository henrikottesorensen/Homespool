namespace Homespool.Host.Authentication;

/// <summary>
/// What a page collected for the <see cref="Schemes.UserPassword"/> scheme on a step-up: the signed-in
/// account's password, typed again before something the session alone should not be allowed to do.
/// No login with it, deliberately - the account is the session's, and letting the page name another
/// would let somebody else's known password stand in for this person's.
/// </summary>
/// <param name="Password">The password, as typed.</param>
public sealed record PasswordCredential(string? Password);
