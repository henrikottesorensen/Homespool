using System;
using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace Homespool.Host.Services;

/// <summary>
/// Translates <see cref="XForwardedOptions"/> onto the framework's
/// <see cref="ForwardedHeadersOptions"/>.
/// </summary>
/// <remarks>
/// Its own type rather than a private method in <c>Program</c> so the mapping can be tested directly:
/// the parsing is where a silent mistake would live, and an unparseable entry must be skipped rather
/// than throwing a deployment into a crash loop over one typo.
/// </remarks>
public static class ForwardedHeadersConfigurator
{
    /// <summary>
    /// Applies <paramref name="source"/> to <paramref name="target"/>, logging anything unusable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The framework defaults are replaced, not extended.</b> They trust loopback; a deployment
    /// that has said which network its proxy is on does not also want loopback trusted, and anything
    /// wider than what was configured is a guess.
    /// </para>
    /// <para>
    /// <b>This is only safe because the caller does not register the middleware at all when nothing
    /// is trusted</b> (<see cref="XForwardedOptions.TrustsAnything"/>). Empty lists do not disable
    /// forwarding — ASP.NET performs its peer check only when at least one entry is present, so
    /// clearing both and adding none makes it honour headers from every client. Never call this and
    /// then use the middleware unconditionally.
    /// </para>
    /// </remarks>
    /// <param name="source">The deployment's configuration.</param>
    /// <param name="target">The framework options to populate.</param>
    /// <param name="onIgnoredEntry">
    /// Called once per unusable entry, with a description. A callback rather than an
    /// <c>ILogger</c> so this stays free of a logging abstraction and a test can simply collect
    /// what it was told.
    /// </param>
    public static void Apply(XForwardedOptions source, ForwardedHeadersOptions target, Action<string>? onIgnoredEntry = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        // Scheme and host as well as the address: absolute URLs - confirmation and password-reset
        // links especially - must be built with the address the user actually reached.
        target.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                                | ForwardedHeaders.XForwardedProto
                                | ForwardedHeaders.XForwardedHost;

        target.ForwardedForHeaderName = source.ClientAddressHeader;
        target.ForwardLimit = source.ForwardLimit;

        target.KnownProxies.Clear();
        target.KnownIPNetworks.Clear();

        foreach (string proxy in source.KnownProxies)
        {
            if (IPAddress.TryParse(proxy, out IPAddress? address))
            {
                target.KnownProxies.Add(address);
            }
            else
            {
                onIgnoredEntry?.Invoke($"XForwarded:KnownProxies entry '{proxy}' is not an IP address and was ignored.");
            }
        }

        foreach (string network in source.KnownNetworks)
        {
            if (System.Net.IPNetwork.TryParse(network, out System.Net.IPNetwork parsed))
            {
                target.KnownIPNetworks.Add(parsed);
            }
            else
            {
                onIgnoredEntry?.Invoke($"XForwarded:KnownNetworks entry '{network}' is not CIDR notation and was ignored.");
            }
        }
    }
}
