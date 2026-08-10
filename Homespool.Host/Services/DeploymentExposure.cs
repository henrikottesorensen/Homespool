using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Homespool.Host.Services;

/// <summary>
/// Decides whether a deployment is exposed in a way that gives its credentials away — the judgement,
/// with no configuration reading, no resolver and no request of its own.
/// </summary>
/// <remarks>
/// <para>
/// <c>internet-exposure.md</c> #4 asked for this, in terms that have since moved: it named
/// <c>PublicHost</c> and <c>PublicTls</c>, from before the rename and before printers and people got
/// separate transports. There are two questions now, and only one of them is answerable from
/// configuration — hence two rules here rather than one.
/// </para>
/// <para>
/// <b>Both are about credentials on the wire, not about tidiness.</b> A printer token authenticates a
/// printer for good; a session cookie is an administrator. Neither is worth a banner for being merely
/// unusual, and both are worth one for being readable by anyone on the path.
/// </para>
/// </remarks>
public static class DeploymentExposure
{
    /// <summary>
    /// Whether printers are being told to send their tokens across the public internet in clear.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Plaintext on a private address is deliberately not reported.</b> It is the supported testing
    /// mode - reading the protocol on the wire, or a rig - and startup already warns about it at every
    /// start. A permanent banner saying the same thing is how a warning becomes wallpaper, and the one
    /// case that genuinely needs shouting about would then be indistinguishable from the noise.
    /// </para>
    /// <para>
    /// <b>An address that resolves to nothing is not evidence.</b> Same rule as everywhere else here:
    /// only a positive answer counts, so a name the server cannot resolve leaves this quiet rather than
    /// guessing at what it might point to.
    /// </para>
    /// </remarks>
    /// <param name="printerTls">Whether the printer transport uses TLS.</param>
    /// <param name="printerHost">The address printers are told to use, or null if none is set.</param>
    /// <param name="resolvedHost">What that address resolves to; empty means the resolver had no answer.</param>
    /// <param name="containerNetworks">Ranges that exist only inside this deployment.</param>
    public static ExposureVerdict EvaluatePrinterTransport(bool printerTls,
                                                           string? printerHost,
                                                           IReadOnlyList<IPAddress> resolvedHost,
                                                           IReadOnlyList<IPNetwork> containerNetworks)
    {
        ArgumentNullException.ThrowIfNull(resolvedHost);
        ArgumentNullException.ThrowIfNull(containerNetworks);

        if (printerTls || string.IsNullOrWhiteSpace(printerHost))
        {
            return new(ExposureState.Ok, "Printers reach this server over TLS.");
        }

        IPAddress[] reachableFromOutside =
            [.. resolvedHost.Where(address => !IsPrivate(address, containerNetworks))];

        if (reachableFromOutside.Length == 0)
        {
            return new(ExposureState.Ok,
                "Printers reach this server over plain HTTP, at an address only your own network can reach.");
        }

        return new(ExposureState.PrinterTokensCrossThePublicInternet,
            $"Printers are told to reach this server at {printerHost.Trim()} over plain HTTP, and that address is "
            + $"reachable from the internet ({string.Join(", ", reachableFromOutside.AsEnumerable())}). Every printer's "
            + "token crosses the internet in clear text, in both directions, and a token identifies a printer "
            + "permanently. Set PrusaConnect:PrinterTls to true, restart, and issue new provisioning bundles - the "
            + "ones already handed out say tls = False.");
    }

    /// <summary>
    /// Whether the administrator reading this is doing so in the clear.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Answered from the request, because it cannot be answered from configuration.</b> This server
    /// deliberately has no user-facing address - links in mail come from whatever the request said - so
    /// there is nothing to inspect at startup. What there is, at exactly the moment it matters, is a
    /// request that either arrived over TLS or did not.
    /// </para>
    /// <para>
    /// <b>Loopback is exempt and nothing else is.</b> A developer on localhost is not exposing
    /// anything; an administrator on the household LAN is putting a session cookie and a typed password
    /// on a wire other people share, which is the network this project's own notes call the place a
    /// credential is worth taking. The threat model here is not "the internet" but "anyone on the
    /// path".
    /// </para>
    /// <para>
    /// It reads <c>isHttps</c> <i>after</i> the forwarded-headers middleware, so a proxy that
    /// terminates TLS and says so is correctly silent - which is the shipped stack, and the case this
    /// must not nag about.
    /// </para>
    /// </remarks>
    /// <param name="isHttps">Whether the request arrived over TLS, as the application sees it.</param>
    /// <param name="client">The client's address, or null if there is none to speak of.</param>
    public static ExposureVerdict EvaluateAdminSession(bool isHttps, IPAddress? client)
    {
        if (isHttps || client is null || IPAddress.IsLoopback(client))
        {
            return new(ExposureState.Ok, "This session is encrypted.");
        }

        // Kestrel hands out IPv4 addresses in their mapped IPv6 form, and "::ffff:192.168.13.44" in a
        // sentence aimed at a person is noise they have to decode before they can recognise their own
        // network.
        IPAddress readable = client.IsIPv4MappedToIPv6 ? client.MapToIPv4() : client;

        return new(ExposureState.SessionInClear,
            $"You are signed in over plain HTTP, from {readable}. Your session cookie - and anything you type, "
            + "including passwords - crosses the network readable by anyone on the path. Put the shipped proxy back "
            + "in front of this server, or set Listeners:UserHttpsPort so it terminates TLS itself.");
    }

    /// <summary>
    /// Whether an address is one only this network can reach.
    /// </summary>
    /// <remarks>
    /// RFC 1918, loopback, link-local and carrier-grade NAT, plus whatever the deployment says is its
    /// own container network. Everything else is treated as reachable from outside - deliberately the
    /// pessimistic reading, since the cost of a wrong "public" verdict is a banner and the cost of a
    /// wrong "private" one is silence about tokens on the internet.
    /// </remarks>
    private static bool IsPrivate(IPAddress address, IReadOnlyList<IPNetwork> containerNetworks)
    {
        if (IPAddress.IsLoopback(address) || containerNetworks.Any(network => network.Contains(address)))
        {
            return true;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            // IPv6 unique-local (fc00::/7) and link-local (fe80::/10); anything else is assumed routable.
            byte first = address.GetAddressBytes()[0];

            return (first & 0xFE) == 0xFC || address.IsIPv6LinkLocal;
        }

        byte[] octets = address.GetAddressBytes();

        return octets[0] switch
        {
            10 => true,
            127 => true,
            169 when octets[1] == 254 => true,
            172 when octets[1] >= 16 && octets[1] <= 31 => true,
            192 when octets[1] == 168 => true,
            100 when octets[1] >= 64 && octets[1] <= 127 => true,
            _ => false,
        };
    }
}
