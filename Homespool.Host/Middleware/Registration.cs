using System;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Serilog;

namespace Homespool.Host.Middleware;

/// <summary>
/// Wires up the pipeline's own options, so <c>Program.cs</c> says what is being added rather than
/// how.
/// </summary>
/// <remarks>
/// <c>Cameras/Registration.cs</c> is the same idea: the configuration for a thing lives beside the
/// thing.
/// </remarks>
public static class Registration
{
    /// <summary>
    /// Translates <see cref="XForwardedOptions"/> onto the framework's forwarded-headers middleware,
    /// and says at startup what it ended up trusting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The framework middleware does the security-relevant part - checking the immediate peer against
    /// the known proxies before believing anything - so this only supplies it with what to trust and
    /// which header to read. Hand-rolling the header parsing was considered and rejected: the entry
    /// selection is exactly where this class of bug lives.
    /// </para>
    /// <para>
    /// <b>An unconfigured deployment is safe but inert</b>, because the middleware is not registered at
    /// all, so no forwarded header is honoured from any peer - loopback included - and a container proxy
    /// gets no more credit than anybody else. That failure is silent - mail keeps saying
    /// <c>http://</c> - so it is logged rather than left to be discovered. This repository has
    /// declared a rule and never run it four times over; this is the same shape, caught at startup.
    /// </para>
    /// <para>
    /// <b>Registering the middleware is the caller's decision, not this one's.</b> Clearing the
    /// framework's known networks and adding nothing does not mean "trust nobody" - ASP.NET skips the
    /// peer check entirely when both lists are empty, which means "trust anybody" - so the pipeline
    /// leaves it out altogether unless <see cref="XForwardedOptions.TrustsAnything"/> is true.
    /// </para>
    /// </remarks>
    public static WebApplicationBuilder AddForwardedHeaders(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        XForwardedOptions forwarded = new();
        builder.Configuration.GetSection(XForwardedOptions.SectionName).Bind(forwarded);

        builder.Services.Configure<XForwardedOptions>(
            builder.Configuration.GetSection(XForwardedOptions.SectionName));

        // Serilog's static, deliberately, here and in the two branches below: this runs while the
        // container is still being described, so there is no host logger to ask for yet. A host no
        // longer replaces that static with its own logger, so what these reach is the bootstrap logger
        // - console only, which is the honest ceiling for anything logged before the application's own
        // logging configuration has been read.
        builder.Services.Configure<ForwardedHeadersOptions>(
            options => ForwardedHeadersConfigurator.Apply(forwarded, options, Log.Warning));

        builder.Services.AddSingleton<ProxyHostAddresses>();

        if (forwarded.TrustsAnything)
        {
            Log.Information("Trusting {Header} from {ProxyCount} proxy address(es) and {NetworkCount} network(s).",
                            forwarded.ClientAddressHeader, forwarded.KnownProxies.Length, forwarded.KnownNetworks.Length);

            if (!string.IsNullOrWhiteSpace(forwarded.ProxyHost))
            {
                Log.Information("Within those, only from whatever {ProxyHost} resolves to.", forwarded.ProxyHost);
            }

            // Trusting the proxy is not enough for a per-address limit: the host may have rewritten
            // every client's address before the proxy saw it, and only the engine can say.
            switch (forwarded.ClientAddressesUnreliable)
            {
                case true:
                    Log.Information("XForwarded:ClientAddressesUnreliable is set: this host gives every client the " +
                                    "same address, so the sign-in and printer-registration limits per address are off.");
                    break;
                case null:
                    Log.Warning("XForwarded:ClientAddressesUnreliable is not set, so the sign-in and printer-registration " +
                                "limits per address are off: under Docker Desktop, rootless Docker or WSL every client " +
                                "arrives with one address. Run setup-env.sh, which asks Docker and sets it.");
                    break;
            }
        }
        else if (!string.IsNullOrWhiteSpace(forwarded.ProxyHost))
        {
            Log.Warning("XForwarded:ProxyHost is {ProxyHost}, but XForwarded:KnownProxies and :KnownNetworks are both " +
                        "empty, so no proxy is trusted at all: the name only narrows those. Set " +
                        "XForwarded:KnownNetworks to the proxy's network.",
                        forwarded.ProxyHost);
        }
        else
        {
            Log.Warning("No proxy is trusted (XForwarded:KnownProxies and :KnownNetworks are both empty), so " +
                        "the forwarded-headers middleware is not registered and forwarded headers are ignored " +
                        "from every peer, loopback included. If this deployment sits behind a " +
                        "reverse proxy, links in outgoing mail will say http://, client addresses in the log " +
                        "will be the proxy's, and the sign-in and printer-registration limits per address are " +
                        "off - they need an address that names one client, and every visitor would otherwise " +
                        "share one window. Set " +
                        "XForwarded:KnownNetworks to the proxy's network.");
        }

        return builder;
    }

    /// <summary>
    /// The forwarded-headers middleware, behind <see cref="ProxyPeerMiddleware"/> when
    /// <see cref="XForwardedOptions.ProxyHost"/> names the proxy.
    /// </summary>
    /// <remarks>
    /// Only for a pipeline that trusts something - see <see cref="AddForwardedHeaders"/> for why the
    /// caller decides that, not this.
    /// </remarks>
    public static IApplicationBuilder UseTrustedForwardedHeaders(this IApplicationBuilder app, XForwardedOptions forwarded)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(forwarded);

        if (!string.IsNullOrWhiteSpace(forwarded.ProxyHost))
        {
            app.UseMiddleware<ProxyPeerMiddleware>();
        }

        return app.UseForwardedHeaders();
    }
}
