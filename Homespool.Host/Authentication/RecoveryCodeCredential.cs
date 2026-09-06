namespace Homespool.Host.Authentication;

/// <summary>
/// What a page collected for the <see cref="Schemes.RecoveryCode"/> scheme: one of the account's
/// recovery codes, as typed, presented through <see cref="CredentialAuthentication"/>.
/// </summary>
/// <param name="Code">The code, as typed.</param>
public sealed record RecoveryCodeCredential(string? Code);
