using System;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace Homespool.Host.Authentication;

/// <summary>
/// What the passkey scheme needs to know about this deployment: the relying-party id a credential is
/// bound to, how long a ceremony may take, and the cookie the ceremony's state rides in between the
/// challenge and the assertion.
/// </summary>
/// <remarks>
/// <para>
/// <b>The relying-party id is the whole configuration.</b> A passkey is minted against one hostname
/// and answers only to a page served from it or from a subdomain of it, so <see cref="ServerDomain"/>
/// has to be the name people type into the browser. An IP address can never be one, <c>localhost</c>
/// is its own, and a name a public certificate authority will not issue for works only where the
/// browser already trusts the certificate served under it. <b>Empty withholds the feature</b> rather
/// than guessing a name from the request: a guess would mint credentials that work on one address and
/// fail silently on every other.
/// </para>
/// <para>
/// <b>The engine reads the framework's own options type</b>, <c>IdentityPasskeyOptions</c>, and
/// learns the two deployment-bound values from here when the scheme is registered - see
/// <see cref="AuthenticationBuilderExtensions.AddPasskeyAuthentication"/>. The fixed policy - user
/// verification required, no attestation - is <c>IdentityConfiguration.ConfigurePasskeys</c>.
/// </para>
/// </remarks>
public class PasskeyAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>The cookie name used unless <see cref="CeremonyCookie"/> sets another.</summary>
    public const string DefaultCeremonyCookieName = $"{PasskeyAuthenticationHandler.PasskeyPrefix}.Ceremony";

    /// <summary>
    /// The form field an assertion is posted in: the <c>PublicKeyCredential</c> the browser returned,
    /// serialised with <c>toJSON()</c>. The scheme's one wire contract with the page that drives it.
    /// </summary>
    public const string CredentialFormField = "credential";

    /// <summary>
    /// The relying-party id: the hostname every passkey of this deployment is bound to. Null or empty
    /// withholds passkeys altogether.
    /// </summary>
    public string? ServerDomain { get; set; }

    /// <summary>
    /// How long the browser has to answer a challenge. It is both the <c>timeout</c> hint the request
    /// options carry and the life of the <see cref="CeremonyCookie"/>, so a challenge that outlives it
    /// is refused by the server whatever the browser did.
    /// </summary>
    public TimeSpan CeremonyLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The cookie carrying the data-protected ceremony state: the challenge and, for a challenge
    /// bound to an account, which account.
    /// </summary>
    /// <remarks>
    /// <b>Strict, deliberately, where the application cookie is Lax.</b> A ceremony is same-origin from
    /// its first byte to its last, so nothing is lost by refusing the cookie on a cross-site
    /// navigation. <b>The path is the page that issued the challenge</b> unless one is set here: the
    /// assertion comes back to that same page, and no other page ever has a reason to read it.
    /// </remarks>
    public CookieBuilder CeremonyCookie { get; set; } = new()
    {
        Name = DefaultCeremonyCookieName,
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        SecurePolicy = CookieSecurePolicy.SameAsRequest,
        IsEssential = true,
    };

    /// <summary>Whether a relying-party id has been configured at all.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServerDomain);

    /// <summary>
    /// Whether a request arriving on <paramref name="host"/> can complete a ceremony against
    /// <see cref="ServerDomain"/>: the host is the relying-party id, or a subdomain of it.
    /// </summary>
    /// <remarks>
    /// The check the browser itself makes, done here first so that the passkey affordance can be
    /// withheld with a reason instead of failing in the ceremony. An IP literal fails it, as it must,
    /// and so does <c>localhost</c> against any real name.
    /// </remarks>
    public bool Covers(HostString host)
    {
        if (!IsConfigured || !host.HasValue)
        {
            return false;
        }

        string domain = ServerDomain!.Trim();
        string name = host.Host;

        return string.Equals(name, domain, StringComparison.OrdinalIgnoreCase) ||
               (name.Length > domain.Length + 1 &&
                name.EndsWith(domain, StringComparison.OrdinalIgnoreCase) &&
                name[name.Length - domain.Length - 1] == '.');
    }

    /// <inheritdoc/>
    public override void Validate()
    {
        base.Validate();

        if (CeremonyLifetime <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(CeremonyLifetime)} must be positive.");
        }

        if (string.IsNullOrEmpty(CeremonyCookie.Name))
        {
            throw new InvalidOperationException($"{nameof(CeremonyCookie)} must have a name.");
        }
    }
}
