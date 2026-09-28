using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Homespool.Host.Notifications.WebPush;

/// <summary>
/// Connects the push client, and only to a public address.
/// </summary>
/// <remarks>
/// <para>
/// <b>The check runs on the address being connected to, which is what closes the gap a name check
/// leaves.</b> <see cref="WebPushEndpointPolicy"/> accepts a push service by name, and a name can
/// resolve to anything by the time a connection is made. Here the resolution and the connection are
/// one step in this process, so there is no second lookup for an answer to change between.
/// </para>
/// <para>
/// <b>Public only, which is stricter than a camera address and deliberately so.</b> A camera lives on
/// the LAN; a push service never does. So private ranges, carrier-grade NAT, link-local, loopback and
/// every reserved block are refused, and the one legitimate destination class loses nothing.
/// </para>
/// </remarks>
public static class PushAddressGuard
{
    /// <summary>
    /// Every block a push service cannot be in. IPv4-mapped IPv6 addresses are unmapped before these
    /// are consulted.
    /// </summary>
    private static readonly IPNetwork[] NonPublic =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"),
        IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"),
        IPNetwork.Parse("224.0.0.0/4"),
        IPNetwork.Parse("240.0.0.0/4"),
        IPNetwork.Parse("::/128"),
        IPNetwork.Parse("::1/128"),

        // NAT64 and its local-use sibling, Teredo and 6to4 all carry an IPv4 address inside, which
        // could be any of the above. No push service is reached through one.
        IPNetwork.Parse("64:ff9b::/96"),
        IPNetwork.Parse("64:ff9b:1::/48"),
        IPNetwork.Parse("2001::/32"),
        IPNetwork.Parse("2002::/16"),
        IPNetwork.Parse("100::/64"),
        IPNetwork.Parse("2001:db8::/32"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("fec0::/10"),
        IPNetwork.Parse("ff00::/8"),
    ];

    /// <summary>
    /// Whether <paramref name="address"/> is somewhere on the public internet a push service could be.
    /// </summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        IPAddress candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        return !NonPublic.Any(network => network.Contains(candidate));
    }

    /// <summary>
    /// A <see cref="SocketsHttpHandler.ConnectCallback"/> that resolves the host, keeps its public
    /// addresses and connects to one of them - IPv4 first, since the container has no IPv6 route and a
    /// AAAA record would otherwise cost a timeout before the fallback.
    /// </summary>
    /// <exception cref="PushAddressRefusedException">The host has no public address.</exception>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context,
                                                       CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        DnsEndPoint endpoint = context.DnsEndPoint;

        IPAddress[] resolved = IPAddress.TryParse(endpoint.Host, out IPAddress? literal) ?
            [literal] :
            await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);

        IPAddress[] allowed = [.. resolved.Where(IsPublicAddress)
                                          .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)];

        if (allowed.Length == 0)
        {
            throw new PushAddressRefusedException(endpoint.Host);
        }

        Socket? socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(allowed, endpoint.Port, cancellationToken).ConfigureAwait(false);

            NetworkStream stream = new(socket, ownsSocket: true);
            socket = null;

            return stream;
        }
        finally
        {
            socket?.Dispose();
        }
    }
}
