namespace Homespool.Host.Authentication;

/// <summary>
/// What a page collected for the <see cref="Schemes.Totp"/> scheme to complete a sign-in: the
/// authenticator code of the account the password step left pending, as typed, presented through
/// <see cref="CredentialAuthentication"/>. Verified against the pending account and no other; a
/// step-up on the signed-in account presents a <see cref="TotpStepUpCredential"/> instead. The scheme
/// strips the spaces and dashes authenticator apps show.
/// </summary>
/// <param name="Code">The code, as typed.</param>
public sealed record TotpCredential(string? Code);
