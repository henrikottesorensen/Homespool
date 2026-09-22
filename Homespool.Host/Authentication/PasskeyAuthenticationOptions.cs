using System;
using System.Collections.Generic;
using System.Linq;

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
    /// The relying-party id: the hostname every passkey of this deployment is bound to. Null or empty
    /// withholds passkeys altogether.
    /// </summary>
    public string? ServerDomain { get; set; }

    /// <summary>
    /// The names people browse this deployment by, from <c>AllowedHosts</c>: a passkey origin must
    /// name one of them exactly. Empty - <c>AllowedHosts</c> unset or a wildcard - admits the
    /// relying-party id alone.
    /// </summary>
    /// <remarks>
    /// Read from configuration, not from the host filter's options, which also carry every name on the
    /// printer certificate. A <c>*.</c> pattern is a subdomain wildcard to the host filter and matches
    /// no origin here, since nothing is matched but a whole name.
    /// </remarks>
    public IReadOnlyList<string> ServedHosts { get; set; } = [];

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
    /// and so does <c>localhost</c> against any real name. It is wider than what a ceremony accepts:
    /// <see cref="AllowsOrigin"/> also wants an exact served name and the served port.
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

    /// <summary>
    /// Whether an assertion or attestation claiming to come from <paramref name="origin"/> may be
    /// accepted on a request that arrived for <paramref name="requestHost"/>: a secure origin on a name
    /// this deployment serves people on, under the relying-party id, on the port the request came in on.
    /// </summary>
    /// <param name="origin">The origin the client data claims.</param>
    /// <param name="requestHost">The <c>Host</c> the assertion was posted to, which supplies the port.</param>
    /// <remarks>
    /// <para>
    /// <b>The name is one of <see cref="ServedHosts"/>, exactly, and <see cref="Covers"/> accepts
    /// it.</b> Covering is not enough on its own. A browser lets a page on any subdomain of the
    /// relying-party id run a ceremony against it, so a page on a subdomain somebody else controls can
    /// carry a challenge that person fetched in their own session, have a visitor's authenticator sign
    /// it, and post the result with their own ceremony cookie. The only trace is the origin in the
    /// client data, which is why the check is on the whole name. With no served names configured, the
    /// relying-party id is the one name accepted.
    /// </para>
    /// <para>
    /// <b>The scheme is <c>https</c></b>, or plain <c>http</c> on <c>localhost</c>, which browsers treat
    /// as secure for a developer's sake.
    /// </para>
    /// <para>
    /// <b>The port is the one in <paramref name="requestHost"/></b>, or the origin scheme's default
    /// when it names none. It is only as trustworthy as that header. Behind the shipped proxy it is
    /// the deployment's: the people-facing TLS listener writes the published port into <c>Host</c>
    /// whatever the client asked for, and no other listener forwards a sign-in page. With nothing in
    /// front of the application, <c>Host</c> is whatever the client sent, and so is the port it
    /// checks against.
    /// </para>
    /// </remarks>
    public bool AllowsOrigin(string? origin, HostString requestHost)
    {
        if (string.IsNullOrEmpty(origin) || !requestHost.HasValue || !Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        bool https = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        bool secure = https ||
                      (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
        int servedPort = requestHost.Port ?? (https ? 443 : 80);

        return secure && Covers(new HostString(uri.Host)) && Serves(uri.Host) && uri.Port == servedPort;
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

    /// <summary>Whether <paramref name="name"/> is a served name, or the relying-party id when none is configured.</summary>
    private bool Serves(string name)
    {
        return ServedHosts.Count == 0 ?
            string.Equals(name, ServerDomain?.Trim(), StringComparison.OrdinalIgnoreCase) :
            ServedHosts.Contains(name, StringComparer.OrdinalIgnoreCase);
    }
}
