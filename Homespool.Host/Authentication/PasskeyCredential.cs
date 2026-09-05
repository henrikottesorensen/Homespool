namespace Homespool.Host.Authentication;

/// <summary>
/// What a page collected for the <see cref="Schemes.Passkey"/> scheme: the <c>PublicKeyCredential</c>
/// the browser returned from the assertion ceremony, serialised with <c>toJSON()</c>, presented
/// through <see cref="CredentialAuthentication"/>.
/// </summary>
/// <param name="Json">The credential as the browser serialised it.</param>
public sealed record PasskeyCredential(string? Json)
{
    /// <summary>
    /// The form field the two passkey scripts post the credential in, and the pages bind it from:
    /// their one wire contract. The scheme itself never reads it.
    /// </summary>
    public const string FormField = "credential";
}
