namespace Homespool.Host.Authentication;

/// <summary>
/// What a page collected for the <see cref="Schemes.Totp"/> scheme on behalf of the signed-in account:
/// an authenticator code verified against the session's account and no other - a browser that also
/// holds a pending sign-in for some other account does not get that account's code accepted here. A
/// wrong one backs off the account's step-ups, never its sign-in.
/// </summary>
/// <remarks>
/// <b>Its one caller is enrolment</b>: <c>Manage/EnableAuthenticator</c> checks the first code a new
/// app shows, which proves the app holds the seed. It is not how a person proves themselves before an
/// act - that is <see cref="RecentProof"/>, earned at <c>Account/Reauthenticate</c> - and a page reaching
/// for this to gate something is reaching for the wrong thing.
/// </remarks>
/// <param name="Code">The code, as typed.</param>
public sealed record TotpStepUpCredential(string? Code);
