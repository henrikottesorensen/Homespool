namespace Homespool.Host.Authentication;

/// <summary>
/// What a page collected for the <see cref="Schemes.Totp"/> scheme: an authenticator code, as typed,
/// presented through <see cref="CredentialAuthentication"/>. The scheme strips the spaces and dashes
/// authenticator apps show.
/// </summary>
/// <param name="Code">The code, as typed.</param>
public sealed record TotpCredential(string? Code);
