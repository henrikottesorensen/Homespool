using System.Collections.Generic;
using System.Linq;
using System.Net;

using AwesomeAssertions;

using Homespool.Host.Certificates;

namespace Homespool.Host.Test;

/// <summary>
/// What first-run setup offers as the address printers should dial.
/// </summary>
/// <remarks>
/// These are suggestions rather than a decision, deliberately: detection reports what is true now,
/// and the question is what stays true — which depends on a router we cannot see
/// (<c>notes/tls-by-default.md</c>, decision 2). What is tested here is that the offer is honest
/// about which candidates are likely to break.
/// </remarks>
public class PrinterAddressSuggestionTests
{
    /// <summary>
    /// What ships in <c>appsettings.json</c>: Docker's own range. A deployment that pins a different
    /// network passes that instead, which is the point of the setting.
    /// </summary>
    private static readonly IReadOnlyList<IPNetwork> DockerDefault = [IPNetwork.Parse("172.16.0.0/12")];

    private static PrinterAddressSuggestion? Find(IReadOnlyList<PrinterAddressSuggestion> all, string value)
    {
        return all.FirstOrDefault(s => s.Value == value);
    }

    /// <summary>
    /// A Docker-range address is offered but flagged as almost certainly the container's own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The trap worth the whole class. <c>compose.yaml</c> is the primary deployment, and inside a
    /// container the detected address is the container's rather than the host's - so the most likely
    /// deployment is exactly where naive detection is confidently wrong, and a bundle built on it
    /// cannot work while giving no clue why.
    /// </para>
    /// <para>
    /// Offered rather than hidden, because suppressing it would leave the page suggesting nothing at
    /// all in that same deployment. The warning is the product.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("172.17.0.2")]
    [InlineData("172.28.0.5")]
    [InlineData("172.31.255.254")]
    public void ADockerRangeAddressIsFlaggedRatherThanTrusted(string address)
    {
        // Act
        IReadOnlyList<PrinterAddressSuggestion> suggestions = PrinterAddressSuggestion.Classify([IPAddress.Parse(address)], hostName: null, DockerDefault);

        // Assert
        Find(suggestions, address)!.Durability.Should().Be(AddressDurability.ProbablyTheContainersOwn);
        Find(suggestions, address)!.Note.Should().Contain("Docker");
    }

    /// <summary>
    /// An ordinary LAN address is offered plainly, with the lease caveat.
    /// </summary>
    /// <remarks>
    /// 172.16/12 is the Docker-ish range; 10/8 and 192.168/16 are not, and must not be tarred with
    /// the same warning or the page cries wolf on the common case.
    /// </remarks>
    [Theory]
    [InlineData("192.168.13.238")]
    [InlineData("10.4.1.9")]
    public void AnOrdinaryPrivateAddressIsOfferedWithTheLeaseCaveat(string address)
    {
        // Act
        IReadOnlyList<PrinterAddressSuggestion> suggestions = PrinterAddressSuggestion.Classify([IPAddress.Parse(address)], hostName: null, DockerDefault);

        // Assert
        Find(suggestions, address)!.Durability.Should().Be(AddressDurability.UntilTheLeaseMoves);
    }

    /// <summary>
    /// Loopback and link-local are not offered at all.
    /// </summary>
    /// <remarks>
    /// No printer can reach either. 169.254/16 in particular means this machine failed to get an
    /// address, so offering it would propose a certificate for a name that never worked.
    /// </remarks>
    [Fact]
    public void UnreachableAddressesAreNotOffered()
    {
        // Act
        IReadOnlyList<PrinterAddressSuggestion> suggestions = PrinterAddressSuggestion.Classify(
            [IPAddress.Parse("127.0.0.1"), IPAddress.Parse("169.254.10.1"), IPAddress.Parse("192.168.1.5")],
            hostName: null,
            DockerDefault);

        // Assert
        suggestions.Select(s => s.Value).Should().BeEquivalentTo(["192.168.1.5"]);
    }

    /// <summary>
    /// A hostname is offered as the durable option, and <c>.local</c> is not offered at all.
    /// </summary>
    /// <remarks>
    /// A name outlives a lease where something resolves it, which is why it leads. <c>.local</c> is
    /// excluded because that is mDNS: it fails the <i>resolution</i> half rather than the matching
    /// half, and firmware does not do mDNS. Offering it would look like the best option and never
    /// work.
    /// </remarks>
    [Fact]
    public void AHostNameLeadsUnlessItIsMdns()
    {
        // Act
        IReadOnlyList<PrinterAddressSuggestion> named = PrinterAddressSuggestion.Classify([IPAddress.Parse("192.168.1.5")], "homespool.lan", DockerDefault);
        IReadOnlyList<PrinterAddressSuggestion> mdns = PrinterAddressSuggestion.Classify([IPAddress.Parse("192.168.1.5")], "homespool.local", DockerDefault);

        // Assert
        named[0].Value.Should().Be("homespool.lan");
        named[0].Durability.Should().Be(AddressDurability.SurvivesALeaseChange);
        mdns.Select(s => s.Value).Should().NotContain("homespool.local", "firmware cannot resolve mDNS");
    }

    /// <summary>
    /// The least likely candidate is offered last.
    /// </summary>
    /// <remarks>
    /// Ordering is the only nudge available without making the choice for the operator: whatever the
    /// page shows first is what most people will take.
    /// </remarks>
    [Fact]
    public void ContainerAddressesSortBelowUsableOnes()
    {
        // Act
        IReadOnlyList<PrinterAddressSuggestion> suggestions = PrinterAddressSuggestion.Classify(
            [IPAddress.Parse("172.17.0.2"), IPAddress.Parse("192.168.1.5")], hostName: null, DockerDefault);

        // Assert
        suggestions.Select(s => s.Value).Should().ContainInOrder("192.168.1.5", "172.17.0.2");
    }

    /// <summary>
    /// Nothing detectable yields no suggestions rather than a broken one.
    /// </summary>
    [Fact]
    public void NothingUsableYieldsNoSuggestions()
    {
        // Assert
        PrinterAddressSuggestion.Classify([IPAddress.Loopback], hostName: null, DockerDefault).Should().BeEmpty();
    }
}
