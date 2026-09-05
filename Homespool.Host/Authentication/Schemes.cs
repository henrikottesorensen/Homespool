namespace Homespool.Host.Authentication;

public static class Schemes
{
    /// <summary>
    /// Printers on the <c>/p</c> endpoints, identified by the <c>Fingerprint</c> and <c>Token</c>
    /// headers their firmware sends on every request and on the WebSocket upgrade. See
    /// <see cref="PrusaConnectPrinterAuthenticationHandler"/>.
    /// </summary>
    public const string PrusaConnectPrinter = "PrusaConnect";

    /// <summary>Personal access tokens on the app API. See <see cref="ApiTokenAuthenticationHandler"/>.</summary>
    public const string ApiToken = "ApiToken";

    /// <summary>
    /// The same personal access tokens, presented in <c>X-Api-Key</c> instead. See
    /// <see cref="XApiKeyAuthenticationHandler"/> for why that is a scheme of its own rather than a
    /// second header on <see cref="ApiToken"/>.
    /// </summary>
    public const string XApiKey = "X-Api-Key";

    /// <summary>
    /// The external OpenID Connect identity provider, registered only when <see cref="OidcOptions"/>
    /// carries one. Unlike the schemes above this is a person's sign-in rather than a machine's, so it
    /// signs into <c>IdentityConstants.ExternalScheme</c> and hands off to <c>Account/ExternalLogin</c>.
    /// </summary>
    public const string ExternalOidc = "oidc";

    /// <summary>
    /// A WebAuthn assertion from a passkey an account enrolled, verified by
    /// <see cref="PasskeyAuthenticationHandler"/>. A person's sign-in, like <see cref="ExternalOidc"/>,
    /// and like it never a default scheme: the login page asks for it by name and decides what a
    /// verified assertion is worth.
    /// </summary>
    public const string Passkey = "Passkey";
}
