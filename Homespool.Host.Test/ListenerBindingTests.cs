using System;
using System.Net;

using AwesomeAssertions;

using Homespool.Host.Listeners;
using Homespool.Host.Middleware;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="ListenerBinding.Resolve"/>: the listeners bind the interfaces facing the trusted proxy
/// and loopback, and nothing else - so a sidecar sharing another of this container's networks has no
/// socket to reach.
/// </summary>
/// <remarks>
/// The interfaces are given, not read from the machine, because what the machine holds is the one
/// thing these must not depend on. <see cref="ListenerBinding.Local"/> is the only reader and is a
/// one-line projection of the framework's own enumeration.
/// </remarks>
public class ListenerBindingTests
{
    private static readonly InterfaceAddress Loopback = new(IPAddress.Loopback, 8);
    private static readonly InterfaceAddress Loopback6 = new(IPAddress.IPv6Loopback, 128);
    private static readonly InterfaceAddress Proxy = new(IPAddress.Parse("172.28.0.5"), 16);
    private static readonly InterfaceAddress Cameras = new(IPAddress.Parse("172.29.0.3"), 16);

    /// <summary>A development machine or a bare run has no proxy network to face, so every interface it is.</summary>
    [Fact]
    public void NoTrustedProxyMeansEveryInterface()
    {
        ListenerBinding binding = ListenerBinding.Resolve(new XForwardedOptions(), [Loopback, Proxy, Cameras]);

        binding.IsAny.Should().BeTrue();
    }

    /// <summary>
    /// The shipped stack: the proxy's network is trusted, the container is on it and on the camera
    /// sidecar's, and only the proxy-facing address is bound - the sidecar's network gets no socket.
    /// </summary>
    [Fact]
    public void TheInterfaceInsideATrustedNetworkIsBoundAndTheOtherIsNot()
    {
        XForwardedOptions forwarded = new() { KnownNetworks = ["172.28.0.0/16"] };

        ListenerBinding binding = ListenerBinding.Resolve(forwarded, [Loopback, Loopback6, Proxy, Cameras]);

        binding.IsAny.Should().BeFalse();
        binding.Addresses.Should().BeEquivalentTo([IPAddress.Loopback, IPAddress.IPv6Loopback, Proxy.Address],
                                                  "the proxy-facing address and both loopbacks, never the camera network's");
    }

    /// <summary>
    /// A proxy trusted by address rather than by network - a fixed address on the default bridge, say
    /// - picks the interface whose own subnet holds that address.
    /// </summary>
    [Fact]
    public void ATrustedProxyAddressPicksTheInterfaceWhoseSubnetHoldsIt()
    {
        XForwardedOptions forwarded = new() { KnownProxies = ["172.17.0.1"] };
        InterfaceAddress bridge = new(IPAddress.Parse("172.17.0.9"), 16);

        ListenerBinding binding = ListenerBinding.Resolve(forwarded, [Loopback, bridge, Cameras]);

        binding.Addresses.Should().BeEquivalentTo([IPAddress.Loopback, bridge.Address]);
    }

    /// <summary>
    /// Only the loopback addresses the interfaces carry are bound: a container with IPv6 off has no
    /// <c>::1</c>, and binding one it does not have would fail the start.
    /// </summary>
    [Fact]
    public void OnlyTheLoopbackAddressesPresentAreBound()
    {
        XForwardedOptions forwarded = new() { KnownNetworks = ["172.28.0.0/16"] };

        ListenerBinding binding = ListenerBinding.Resolve(forwarded, [Loopback, Proxy]);

        binding.Addresses.Should().BeEquivalentTo([IPAddress.Loopback, Proxy.Address]);
    }

    /// <summary>An IPv6 proxy network is matched the same way.</summary>
    [Fact]
    public void AnIpv6TrustedNetworkIsMatched()
    {
        XForwardedOptions forwarded = new() { KnownNetworks = ["fd00:28::/64"] };
        InterfaceAddress proxy6 = new(IPAddress.Parse("fd00:28::7"), 64);

        ListenerBinding binding = ListenerBinding.Resolve(forwarded, [Loopback6, proxy6, Cameras]);

        binding.Addresses.Should().BeEquivalentTo([IPAddress.IPv6Loopback, proxy6.Address]);
    }

    /// <summary>
    /// A trusted proxy that no interface faces is a disagreement between the compose file and the
    /// setting, and the process refuses to start rather than bind everywhere or nowhere - naming both
    /// sides, so the message is the diagnosis.
    /// </summary>
    [Fact]
    public void ATrustedProxyNoInterfaceFacesRefusesToStart()
    {
        XForwardedOptions forwarded = new() { KnownNetworks = ["172.28.0.0/16"] };

        Action resolve = () => ListenerBinding.Resolve(forwarded, [Loopback, Cameras]);

        resolve.Should().Throw<InvalidOperationException>()
               .WithMessage("*172.28.0.0/16*")
               .WithMessage("*172.29.0.3/16*");
    }

    /// <summary>Loopback alone is never "facing the proxy", even inside a trusted network that happens to cover it.</summary>
    [Fact]
    public void LoopbackDoesNotCountAsFacingTheProxy()
    {
        XForwardedOptions forwarded = new() { KnownNetworks = ["127.0.0.0/8"] };

        Action resolve = () => ListenerBinding.Resolve(forwarded, [Loopback]);

        resolve.Should().Throw<InvalidOperationException>();
    }
}
