using System;
using System.Net;
using System.Net.Sockets;

namespace Homespool.Host.RateLimiting;

/// <summary>
/// The partition key a per-address window counts a client under.
/// </summary>
/// <remarks>
/// <para>
/// <b>An IPv6 client is its /64, not its address.</b> A /64 is the smallest block a subscriber is
/// given, and every host on it picks its own addresses inside it - privacy addresses rotate there
/// without anybody asking - so a window per full address is a window per request to anyone who
/// wants one. Keyed on the /64, one subscriber is one window, and two households never share one.
/// A caller holding a wider block still gets a window per /64 in it; that is accepted.
/// </para>
/// <para>
/// <b>An IPv4 address seen through an IPv6 socket is keyed as IPv4.</b> A dual-mode listener reports
/// an IPv4 peer as <c>::ffff:a.b.c.d</c>, and masking that to its /64 would put the whole IPv4
/// internet in one window.
/// </para>
/// </remarks>
public static class ClientAddressKey
{
    /// <summary>The prefix length an IPv6 client is keyed on.</summary>
    public const int IPv6PrefixLength = 64;

    /// <summary>
    /// The key every connection with no address shares - a test host, or a unix socket.
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// The key <paramref name="address"/> is counted under: an IPv4 address as itself, an IPv6 one as
    /// its /64 in CIDR form.
    /// </summary>
    public static string Of(IPAddress? address)
    {
        if (address is null)
        {
            return Unknown;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        byte[] bytes = address.GetAddressBytes();

        bytes.AsSpan(IPv6PrefixLength / 8).Clear();

        return $"{new IPAddress(bytes)}/{IPv6PrefixLength}";
    }
}
