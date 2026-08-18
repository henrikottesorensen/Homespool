using System.Collections.Generic;
using System.Net;

using AwesomeAssertions;

using Homespool.Host.Cameras;

namespace Homespool.Host.Test;

/// <summary>
/// Working out the address a browser should send WebRTC media to.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the setting that fails silently when it is wrong</b>, which is why it is derived
/// rather than typed: told an address it cannot reach, a browser negotiates successfully, reports
/// no error, and shows a black rectangle. The rules below are what keep an address that could never
/// work from being handed out in the first place.
/// </para>
/// <para>
/// Addresses rather than a resolver, because the resolving belongs to the caller — the judgement
/// being tested is which of the answers is usable.
/// </para>
/// </remarks>
public class WebRtcCandidateTests
{
    /// <summary>
    /// No operator override, so the address is derived. Named rather than written as a literal
    /// because it is the ordinary case, and reads as an accident where it is not.
    /// </summary>
    private const string EmptyOverride = "";

    /// <summary>
    /// Docker's default bridge range, which is what the application's own interfaces look like from
    /// inside the stack — and exactly what must not be advertised.
    /// </summary>
    private static readonly IReadOnlyList<IPNetwork> ContainerNetworks =
        [new IPNetwork(IPAddress.Parse("172.28.0.0"), 16)];

    /// <summary>
    /// The ordinary deployment: a name that resolves to this machine's LAN address, and the
    /// published media port appended.
    /// </summary>
    [Fact]
    public void ALanAddressIsUsedWithThePublishedPort()
    {
        Candidate(configured: EmptyOverride, resolved: ["192.168.13.183"]).Should().Be("192.168.13.183:8555");
    }

    /// <summary>
    /// The failure this whole derivation exists to prevent. Left alone the sidecar advertises its
    /// own container address, and answering with one here would be doing the same thing by hand.
    /// </summary>
    [Fact]
    public void AContainerAddressIsNotUsed()
    {
        Candidate(configured: EmptyOverride, resolved: ["172.28.0.3"]).Should().BeEmpty();
    }

    [Fact]
    public void LoopbackIsNotUsed()
    {
        Candidate(configured: EmptyOverride, resolved: ["127.0.0.1"]).Should().BeEmpty();
    }

    /// <summary>
    /// A failed DHCP lease. It resolves and it is useless, which is the combination worth refusing.
    /// </summary>
    [Fact]
    public void ALinkLocalAddressIsNotUsed()
    {
        Candidate(configured: EmptyOverride, resolved: ["169.254.10.20"]).Should().BeEmpty();
    }

    /// <summary>
    /// A name resolving to several addresses, which is the ordinary case on a machine with more than
    /// one interface. The container one is skipped rather than taken because it came first.
    /// </summary>
    [Fact]
    public void TheFirstUsableAddressWins()
    {
        Candidate(configured: EmptyOverride, resolved: ["172.28.0.3", "192.168.13.183"])
            .Should().Be("192.168.13.183:8555");
    }

    /// <summary>
    /// Nothing to resolve — no <c>PRINTER_HOST</c>, or a name that answers nothing. Live view is off
    /// rather than guessed at, and the health check is what says so out loud.
    /// </summary>
    [Fact]
    public void NothingResolvedMeansNoLiveView()
    {
        Candidate(configured: EmptyOverride, resolved: []).Should().BeEmpty();
    }

    /// <summary>
    /// The override, and it is deliberately not judged: the case it exists for is a forwarded port
    /// or a tunnel, where the right answer is an address this machine does not hold. Checking it
    /// against what this machine can see would refuse precisely the deployments that need it.
    /// </summary>
    [Fact]
    public void AConfiguredAddressWinsOverAResolvedOne()
    {
        Candidate(configured: "203.0.113.9:8555", resolved: ["192.168.13.183"])
            .Should().Be("203.0.113.9:8555");
    }

    [Fact]
    public void AConfiguredAddressWithNoPortTakesThePublishedOne()
    {
        Candidate(configured: "203.0.113.9", resolved: []).Should().Be("203.0.113.9:8555");
    }

    [Fact]
    public void AConfiguredAddressKeepsItsOwnPort()
    {
        Candidate(configured: "203.0.113.9:9555", resolved: []).Should().Be("203.0.113.9:9555");
    }

    /// <summary>
    /// The one that fails silently, found on the board: an unbracketed IPv6 literal with a port
    /// appended is accepted by the sidecar's configuration and then produces <i>no candidate lines at
    /// all</i>. Not an error — an answer with nothing to connect to.
    /// </summary>
    [Fact]
    public void ABareIPv6AddressIsBracketedBeforeThePortIsAdded()
    {
        Candidate(configured: "fdc2:74d8:1010::bae", resolved: []).Should().Be("[fdc2:74d8:1010::bae]:8555");
    }

    [Fact]
    public void ABracketedIPv6AddressWithoutAPortTakesThePublishedOne()
    {
        Candidate(configured: "[fdc2:74d8:1010::bae]", resolved: []).Should().Be("[fdc2:74d8:1010::bae]:8555");
    }

    [Fact]
    public void ABracketedIPv6AddressKeepsItsOwnPort()
    {
        Candidate(configured: "[fdc2:74d8:1010::bae]:9555", resolved: []).Should().Be("[fdc2:74d8:1010::bae]:9555");
    }

    [Fact]
    public void AHostnameWithNoPortTakesThePublishedOne()
    {
        Candidate(configured: "homespool.lan", resolved: []).Should().Be("homespool.lan:8555");
    }

    [Fact]
    public void SurroundingSpaceIsNotPartOfTheAddress()
    {
        Candidate(configured: "  203.0.113.9:8555  ", resolved: []).Should().Be("203.0.113.9:8555");
    }

    /// <summary>
    /// A published port other than the default, which has to reach the candidate — the address a
    /// browser is given and the port Docker opened are one fact written in two places.
    /// </summary>
    [Fact]
    public void ThePublishedPortIsTheOneAdvertised()
    {
        WebRtcConfigurer.CandidateFor(EmptyOverride, 9555, [IPAddress.Parse("192.168.13.183")], ContainerNetworks)
                        .Should().Be("192.168.13.183:9555");
    }

    private static string Candidate(string configured, string[] resolved)
    {
        List<IPAddress> addresses = [];

        foreach (string address in resolved)
        {
            addresses.Add(IPAddress.Parse(address));
        }

        return WebRtcConfigurer.CandidateFor(configured, 8555, addresses, ContainerNetworks);
    }
}
