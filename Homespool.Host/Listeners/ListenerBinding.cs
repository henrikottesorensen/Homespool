using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

using Homespool.Host.Middleware;

namespace Homespool.Host.Listeners;

/// <summary>
/// Which addresses the listeners bind: the interfaces that face the trusted proxy, and loopback, so
/// that no other network this container joins gets a socket.
/// </summary>
/// <remarks>
/// <para>
/// <b>A container on two networks answers on both unless told otherwise.</b> The shipped stack puts
/// this process on the proxy's network and on the camera sidecar's, because it has to reach go2rtc
/// by name; Docker networks are symmetric, so the sidecar can reach back, and every listener bound to
/// every interface answers it - without nginx, without TLS, without host filtering. Nothing on that
/// network has any business connecting here: every connection is this process calling the sidecar.
/// So rather than deny the sidecar, the listeners are not there to be found: they bind only the
/// interfaces whose address lies inside <see cref="XForwardedOptions.KnownNetworks"/>, or whose
/// subnet holds a <see cref="XForwardedOptions.KnownProxies"/> address, which is the same
/// configuration that already says where the proxy is.
/// </para>
/// <para>
/// <b>Loopback is always bound</b>, because the image's health check asks <c>localhost</c>. Only the
/// loopback addresses the interfaces actually carry, since a container with IPv6 turned off has no
/// <c>::1</c> to bind.
/// </para>
/// <para>
/// <b>No trusted proxy means every interface</b>, which is a development machine or a bare run, where
/// there is no proxy network to face. And a trusted proxy that no interface faces is refused at
/// startup rather than bound everywhere: it means the compose file and the setting disagree, and a
/// process that quietly answered on every interface would be exactly what this exists to prevent.
/// </para>
/// </remarks>
public sealed class ListenerBinding
{
    private ListenerBinding(IReadOnlyList<IPAddress> addresses)
    {
        Addresses = addresses;
    }

    /// <summary>Every interface: the binding when no proxy is trusted.</summary>
    public static ListenerBinding Any { get; } = new([]);

    /// <summary>The addresses to bind, or empty for <see cref="Any"/>.</summary>
    public IReadOnlyList<IPAddress> Addresses { get; }

    /// <summary>Whether the listeners bind every interface rather than the ones listed.</summary>
    public bool IsAny => Addresses.Count == 0;

    /// <summary>
    /// Decides the binding from what <paramref name="forwarded"/> trusts and the addresses
    /// <paramref name="interfaces"/> hold.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A proxy is trusted and no interface faces it: the process would answer on nothing the proxy
    /// can reach, or on everything, and neither is a deployment that should start.
    /// </exception>
    public static ListenerBinding Resolve(XForwardedOptions forwarded, IEnumerable<InterfaceAddress> interfaces)
    {
        ArgumentNullException.ThrowIfNull(forwarded);
        ArgumentNullException.ThrowIfNull(interfaces);

        if (!forwarded.TrustsAnything)
        {
            return Any;
        }

        // Entries that do not parse are ignored here as ForwardedHeadersConfigurator ignores them,
        // which has already warned about each one.
        List<IPNetwork> networks = forwarded.KnownNetworks
                                            .Select(network => IPNetwork.TryParse(network, out IPNetwork parsed) ? parsed : (IPNetwork?)null)
                                            .OfType<IPNetwork>()
                                            .ToList();
        List<IPAddress> proxies = forwarded.KnownProxies
                                           .Select(proxy => IPAddress.TryParse(proxy, out IPAddress? parsed) ? parsed : null)
                                           .OfType<IPAddress>()
                                           .ToList();

        List<InterfaceAddress> held = interfaces.ToList();
        List<IPAddress> facing = held.Where(candidate => !IPAddress.IsLoopback(candidate.Address) &&
                                                         (networks.Any(network => network.Contains(candidate.Address)) ||
                                                          proxies.Any(proxy => Subnet(candidate).Contains(proxy))))
                                     .Select(candidate => candidate.Address)
                                     .Distinct()
                                     .ToList();

        if (facing.Count == 0)
        {
            throw new InvalidOperationException(
                "XForwarded trusts a proxy that no network interface faces: " +
                $"KnownNetworks [{string.Join(", ", forwarded.KnownNetworks)}], KnownProxies [{string.Join(", ", forwarded.KnownProxies)}], " +
                $"while this process holds [{string.Join(", ", held.Select(candidate => $"{candidate.Address}/{candidate.PrefixLength}"))}]. " +
                "The listeners bind only the interfaces facing the trusted proxy, so nothing would be reachable. " +
                "Set XForwarded:KnownNetworks to the network this container shares with the proxy, or clear it to bind every interface.");
        }

        List<IPAddress> loopback = held.Select(candidate => candidate.Address)
                                       .Where(IPAddress.IsLoopback)
                                       .Distinct()
                                       .ToList();

        return new ListenerBinding([.. loopback, .. facing]);
    }

    /// <summary>The addresses this process's interfaces hold right now, loopback included, on interfaces that are up.</summary>
    public static IEnumerable<InterfaceAddress> Local()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
                               .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                               .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                               .Select(unicast => new InterfaceAddress(unicast.Address, unicast.PrefixLength))
                               .ToList();
    }

    /// <summary>The subnet <paramref name="candidate"/> is on, as the network address and its prefix.</summary>
    private static IPNetwork Subnet(InterfaceAddress candidate)
    {
        byte[] bytes = candidate.Address.GetAddressBytes();
        int prefix = Math.Clamp(candidate.PrefixLength, 0, bytes.Length * 8);

        for (int index = 0; index < bytes.Length; index++)
        {
            int bitsKept = Math.Clamp(prefix - (index * 8), 0, 8);
            bytes[index] = (byte)(bytes[index] & (byte)(0xFF << (8 - bitsKept)));
        }

        return new IPNetwork(new IPAddress(bytes), prefix);
    }
}
