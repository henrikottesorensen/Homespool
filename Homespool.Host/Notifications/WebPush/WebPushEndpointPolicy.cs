using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

using Microsoft.Extensions.Options;

namespace Homespool.Host.Notifications.WebPush;

/// <summary>
/// Which push endpoints Homespool will send to: the known push services' own addresses, and nothing a
/// browser - or somebody posting the subscribe form by hand - can point elsewhere.
/// </summary>
/// <remarks>
/// <para>
/// <b>The endpoint is the one address a person supplies that this process itself connects to.</b>
/// Every other address somebody types is fetched by the camera sidecar, if at all. Left unchecked, the
/// subscribe form would be a way to make the server POST to any address it can reach, from inside the
/// deployment's network.
/// </para>
/// <para>
/// <b>An allowlist of names, because the set is small and known.</b> A browser does not choose its
/// push service; its vendor does, and there are four: Google's for Chrome and its relatives, Mozilla's,
/// Apple's and Microsoft's. An operator whose browser uses another adds it to
/// <see cref="WebPushOptions.AdditionalEndpointHosts"/>.
/// </para>
/// <para>
/// <b>A name is not an address, so this is half of the check.</b> The connection itself is refused
/// anything but a public address by <see cref="PushAddressGuard"/>, which runs on the address actually
/// connected to - so a listed name that resolved somewhere private still reaches nothing.
/// </para>
/// </remarks>
public sealed class WebPushEndpointPolicy
{
    /// <summary>
    /// The push services the major browsers subscribe through. A leading <c>*.</c> means any name under
    /// the rest.
    /// </summary>
    /// <remarks>
    /// In order: Chrome and every browser built on it that keeps Google's push service; Firefox;
    /// Safari, on macOS and on iOS from a Home Screen web app; and Edge, whose endpoints name a regional
    /// front door such as <c>wns2-par02p.notify.windows.com</c>.
    /// </remarks>
    public static readonly IReadOnlyList<string> KnownHosts =
    [
        "fcm.googleapis.com",
        "updates.push.services.mozilla.com",
        "web.push.apple.com",
        "*.notify.windows.com",
    ];

    private readonly IOptionsMonitor<WebPushOptions> _options;

    public WebPushEndpointPolicy(IOptionsMonitor<WebPushOptions> options)
    {
        _options = options;
    }

    /// <summary>
    /// Whether Homespool may send to <paramref name="endpoint"/>.
    /// </summary>
    /// <param name="endpoint">The push endpoint a browser subscribed with.</param>
    public bool Allows(string? endpoint)
    {
        return Allows(endpoint, KnownHosts.Concat(_options.CurrentValue.AdditionalEndpointHosts));
    }

    /// <summary>
    /// Whether <paramref name="endpoint"/> is an <c>https</c> address on the default port, carrying no
    /// credential, whose host is one of <paramref name="hosts"/>.
    /// </summary>
    /// <remarks>
    /// <b>Port 443 only.</b> Every push service uses it, and a request's VAPID audience is written
    /// without a port - so an endpoint on another port would carry a signature for a different origin
    /// than the one it is sent to.
    /// </remarks>
    /// <param name="endpoint">The endpoint to check.</param>
    /// <param name="hosts">The hosts accepted, each a name or <c>*.</c> and a name.</param>
    public static bool Allows(string? endpoint, IEnumerable<string> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);

        if (string.IsNullOrWhiteSpace(endpoint) ||
            endpoint.Length > Model.Entities.WebPushDestination.EndpointMaxLength)
        {
            return false;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.Port != 443 ||
            uri.UserInfo.Length > 0 ||
            uri.Fragment.Length > 0)
        {
            return false;
        }

        // A literal address is never a push service's, and would otherwise be matched as a name.
        if (IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out _))
        {
            return false;
        }

        string host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();

        return hosts.Any(pattern => Matches(host, pattern));
    }

    /// <summary>
    /// Whether <paramref name="host"/> is <paramref name="pattern"/>, or, for <c>*.name</c>, a name
    /// under it.
    /// </summary>
    private static bool Matches(string host, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        string wanted = pattern.Trim().TrimEnd('.').ToLowerInvariant();

        if (wanted.StartsWith("*.", StringComparison.Ordinal))
        {
            // The dot is kept in the suffix, so "evilnotify.windows.com" is not under "notify.windows.com".
            string suffix = wanted[1..];

            return host.Length > suffix.Length &&
                   host.EndsWith(suffix, StringComparison.Ordinal);
        }

        return string.Equals(host, wanted, StringComparison.Ordinal);
    }
}
