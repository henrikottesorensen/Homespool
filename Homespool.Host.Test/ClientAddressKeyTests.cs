using System.Net;

using AwesomeAssertions;

using Homespool.Host.RateLimiting;

namespace Homespool.Host.Test;

/// <summary>
/// The key a per-address window counts a client under: an IPv4 address as itself, an IPv6 one as its
/// /64.
/// </summary>
public sealed class ClientAddressKeyTests
{
    private static string KeyOf(string address)
    {
        return ClientAddressKey.Of(IPAddress.Parse(address));
    }

    [Fact]
    public void AnIPv4AddressIsItsOwnKey()
    {
        KeyOf("203.0.113.7").Should().Be("203.0.113.7");
    }

    /// <summary>
    /// Every address a host can pick inside its /64 is the same caller - the case the key exists for.
    /// </summary>
    [Theory]
    [InlineData("2001:db8:1:2::1")]
    [InlineData("2001:db8:1:2:ffff:ffff:ffff:ffff")]
    [InlineData("2001:db8:1:2:a1b2:c3d4:e5f6:789")]
    public void AnIPv6AddressIsKeyedOnItsSlash64(string address)
    {
        KeyOf(address).Should().Be("2001:db8:1:2::/64");
    }

    /// <summary>The neighbouring /64 is another subscriber, and another window.</summary>
    [Fact]
    public void TheNextSlash64IsAnotherKey()
    {
        KeyOf("2001:db8:1:3::1").Should().NotBe(KeyOf("2001:db8:1:2::1"));
    }

    /// <summary>
    /// An IPv4 peer on a dual-mode socket keeps its own window. Masked as IPv6, every IPv4 client would
    /// share <c>::ffff:0:0/64</c> - one window for the whole IPv4 internet.
    /// </summary>
    [Fact]
    public void AnIPv4AddressSeenThroughAnIPv6SocketIsKeyedAsIPv4()
    {
        KeyOf("::ffff:203.0.113.7").Should().Be("203.0.113.7");
        KeyOf("::ffff:198.51.100.9").Should().NotBe(KeyOf("::ffff:203.0.113.7"));
    }

    /// <summary>A link-local address's zone names an interface on this host, not the client.</summary>
    [Fact]
    public void AZoneIsNotPartOfTheKey()
    {
        KeyOf("fe80::1%2").Should().Be("fe80::/64");
    }

    [Fact]
    public void NoAddressIsTheSharedUnknownKey()
    {
        ClientAddressKey.Of(null).Should().Be(ClientAddressKey.Unknown);
    }
}
