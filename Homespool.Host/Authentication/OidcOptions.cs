namespace Homespool.Host.Authentication;

/// <summary>
/// The one external OpenID Connect identity provider, bound from the <c>Oidc</c> configuration
/// section. Absent configuration means no provider is registered and nothing changes.
/// </summary>
/// <remarks>
/// <para>
/// <b>One provider, not a list.</b> A self-hosted deployment has an identity provider or it does not;
/// the shape that would let it have three is a collection everywhere, an <c>Id</c> on each entry to
/// key the scheme by, and a login page that renders a list rather than a button. Nothing here needs
/// that, and a second provider is a widening this shape does not obstruct.
/// </para>
/// <para>
/// <b><see cref="AllowInviteMatchByEmail"/> is the security decision in this file</b> and is the
/// reason it is a setting rather than a constant. See its own documentation.
/// </para>
/// </remarks>
public class OidcOptions
{
    public const string SectionName = "Oidc";

    /// <summary>
    /// The provider's issuer URL, from which discovery reads everything else. Empty disables the whole
    /// feature — see <see cref="IsConfigured"/>.
    /// </summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>The client id this deployment is registered under at the provider.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// The client secret. Required: the authorisation-code flow is used with a confidential client,
    /// PKCE alongside rather than instead of a secret.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// What the provider is called on screen. Not the scheme name, which is
    /// <see cref="Schemes.ExternalOidc"/> and never shown.
    /// </summary>
    public string DisplayName { get; set; } = "Single sign-on";

    /// <summary>
    /// Where the provider redirects back to. Must match the redirect URI registered at the provider.
    /// </summary>
    public string CallbackPath { get; set; } = "/signin-oidc";

    /// <summary>
    /// Whether discovery must be fetched over HTTPS. <b>True, and it stays true in production.</b>
    /// </summary>
    /// <remarks>
    /// The one legitimate reason to turn it off is a provider on loopback during development or in the
    /// test rig — <c>Homespool.Host.IntegrationTest/start-dex.sh</c> runs dex over plain HTTP, because
    /// giving it a certificate would test the fixture's TLS rather than this handler's protocol
    /// handling. Off against a remote provider means the discovery document, and with it the token
    /// endpoint and the signing keys, are whatever the network says they are.
    /// </remarks>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Whether an external sign-in with no invite token in hand may claim an outstanding invite by
    /// matching the address the provider asserts. <b>Off by default.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registration is invite-only, and an invite is normally a bearer secret: the invitee proves they
    /// are the invitee by holding the token from their accept link. An external sign-in that begins at
    /// the login page carries no such token, so with this on, the proof becomes the provider's
    /// <c>email_verified</c> claim instead — which is why the callback refuses when that claim is
    /// absent or false, rather than treating a missing claim as permission.
    /// </para>
    /// <para>
    /// <b>What turning it on costs:</b> a provider that lets a person set their own address and calls
    /// it verified lets that person claim any invite outstanding for it, inside its lifetime. That is
    /// a judgement about a specific provider an operator chose and runs, which is exactly why it is
    /// their decision and not a default. Off, account creation still works — it just has to start from
    /// the invite link, which carries the token.
    /// </para>
    /// </remarks>
    public bool AllowInviteMatchByEmail { get; set; }

    /// <summary>
    /// Whether a provider is configured at all. Nothing is registered when this is false, so
    /// <c>GetExternalAuthenticationSchemesAsync</c> stays empty and every existing guard on it holds.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Authority)
                                && !string.IsNullOrWhiteSpace(ClientId)
                                && !string.IsNullOrWhiteSpace(ClientSecret);
}
