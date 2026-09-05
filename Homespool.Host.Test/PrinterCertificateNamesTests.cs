using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Homespool.Host.Certificates;
using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

/// <summary>
/// What goes into a certificate when one is issued — and, more to the point, what does not.
/// </summary>
/// <remarks>
/// <para>
/// The multi-name hedge exists so an operator who picks the wrong <i>reachable</i> name costs
/// themselves a re-download rather than a reissue. It was never meant to cover names that are wrong by
/// construction: inside a container, detection sees the container's own address and hostname, and no
/// printer can route to either.
/// </para>
/// <para>
/// Keeping them out is not only tidiness. The leaf is presented on the printer port to anything that
/// connects, so a certificate naming the internal network tells every unauthenticated connection what
/// that network looks like - on a deployment this project assumes is internet-facing.
/// </para>
/// </remarks>
public class PrinterCertificateNamesTests
{
    private sealed class Resolver : IHostAddressResolver
    {
        private readonly Dictionary<string, IPAddress[]> _answers;

        public Resolver(Dictionary<string, IPAddress[]> answers)
        {
            _answers = answers;
        }

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string name, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<IPAddress>>(
                _answers.TryGetValue(name, out IPAddress[]? found) ? found : []);
        }
    }

    /// <summary>
    /// The configured host survives even when this machine cannot resolve it — which is the ordinary
    /// case inside a container, and the one where dropping it would be worst.
    /// </summary>
    [Fact]
    public async Task TheConfiguredHostIsNeverFilteredAsync()
    {
        // Arrange - a resolver that knows nothing, as a container's often does about the LAN.
        PrusaConnectOptions connect = new() { PrinterHost = "homespool.lan" };

        // Act
        IReadOnlyList<string> names = await PrinterCertificateNames.ForThisMachineAsync(
            connect, [IPNetwork.Parse("172.16.0.0/12")], new Resolver([]), CancellationToken.None);

        // Assert
        names.Should().Contain("homespool.lan",
                               "it is the operator's declared answer, and unresolvable from here is not unreachable from a printer");
        names[0].Should().Be("homespool.lan", "the first name becomes the certificate's subject");
    }

    /// <summary>
    /// A configured host is kept whatever it looks like, including one inside a container range -
    /// because an operator who names it has said something this code has no standing to overrule.
    /// </summary>
    [Fact]
    public async Task EvenAnOddConfiguredHostIsKeptAsync()
    {
        PrusaConnectOptions connect = new() { PrinterHost = "172.28.0.2" };

        IReadOnlyList<string> names = await PrinterCertificateNames.ForThisMachineAsync(
            connect, [IPNetwork.Parse("172.16.0.0/12")], new Resolver([]), CancellationToken.None);

        names.Should().Contain("172.28.0.2");
    }

    /// <summary>
    /// The configured host is covered alongside the address it points at, so neither choice of what to
    /// write into a printer's ini is a dead end.
    /// </summary>
    /// <remarks>
    /// The resolved address is deliberately one no test machine can have on an interface
    /// (RFC 5737 TEST-NET-2). Detection reads real interfaces, so an ordinary LAN address here would
    /// pass whether expansion worked or not.
    /// </remarks>
    [Fact]
    public async Task TheConfiguredHostIsCoveredAlongsideWhatItResolvesToAsync()
    {
        // Arrange - a container, where the machine's LAN address is on no interface this process can
        // see and resolving the configured name is the only route to it.
        PrusaConnectOptions connect = new() { PrinterHost = "homespool.lan" };
        Resolver resolver = new(new() { ["homespool.lan"] = [IPAddress.Parse("198.51.100.7")] });

        // Act
        IReadOnlyList<string> names = await PrinterCertificateNames.ForThisMachineAsync(
            connect, [IPNetwork.Parse("172.16.0.0/12")], resolver, CancellationToken.None);

        // Assert
        names.Should().Contain("198.51.100.7",
                               "a printer whose DNS cannot answer needs the address, and nothing else can supply it in a container");
        names[0].Should().Be("homespool.lan",
                             "expansion adds to the configured host rather than displacing it, and the first name is the subject");
    }

    /// <summary>
    /// Expansion inherits the rule that keeps the container's own addresses out of the certificate: what
    /// the configured host resolves to is covered only where a printer could reach it.
    /// </summary>
    [Fact]
    public async Task WhatTheConfiguredHostResolvesToIsFilteredLikeAnythingElseAsync()
    {
        // Arrange - every category a resolver hands back that no printer on the LAN can use.
        PrusaConnectOptions connect = new() { PrinterHost = "homespool.lan" };
        Resolver resolver = new(new()
        {
            ["homespool.lan"] =
            [
                IPAddress.Parse("172.31.9.9"), // a container range
                IPAddress.Parse("127.0.0.1"), // this machine, named from this machine
                IPAddress.Parse("169.254.4.9"), // a lease that never arrived
                IPAddress.Parse("fdc2:74d8:1010::cd4"), // the firmware's stack has no IPv6
            ],
        });

        // Act
        IReadOnlyList<string> names = await PrinterCertificateNames.ForThisMachineAsync(
            connect, [IPNetwork.Parse("172.16.0.0/12")], resolver, CancellationToken.None);

        // Assert
        names.Should().Contain("homespool.lan", "the configured host is kept whatever it resolves to");
        names.Should().NotContain("172.31.9.9").And.NotContain("127.0.0.1")
             .And.NotContain("169.254.4.9").And.NotContain("fdc2:74d8:1010::cd4",
                                                           "covering an address no printer can reach hedges nothing and advertises the internal network");
    }

    /// <summary>
    /// The hosts-file answer: the configured name resolves to 127.0.1.1 and nothing else, which the
    /// filters correctly drop and which then reads as "this machine has no address but its name".
    /// </summary>
    [Fact]
    public void AnAnswerOfOnlyLoopbackIsTheHostsFileCase()
    {
        PrinterCertificateNames.ResolvesOnlyToLoopback([IPAddress.Parse("127.0.1.1")]).Should().BeTrue();
    }

    /// <summary>No answer is not the loopback case; that is a name this container cannot resolve, which is ordinary.</summary>
    [Fact]
    public void NoAnswerIsNotTheLoopbackCase()
    {
        PrinterCertificateNames.ResolvesOnlyToLoopback([]).Should().BeFalse();
    }

    /// <summary>Loopback beside a usable address is fine — the usable one is used.</summary>
    [Fact]
    public void LoopbackBesideAUsableAddressIsNotTheLoopbackCase()
    {
        PrinterCertificateNames.ResolvesOnlyToLoopback([IPAddress.Parse("127.0.1.1"), IPAddress.Parse("192.168.13.108")])
                               .Should().BeFalse();
    }

    /// <summary>The configured-host form asks the resolver about the configured name, and says no when none is set.</summary>
    [Fact]
    public async Task TheConfiguredHostFormResolvesTheConfiguredNameAsync()
    {
        Resolver resolver = new(new Dictionary<string, IPAddress[]> { ["homespool.lan"] = [IPAddress.Parse("127.0.1.1")] });

        (await PrinterCertificateNames.ConfiguredHostResolvesOnlyToLoopbackAsync(
             new PrusaConnectOptions { PrinterHost = "homespool.lan" }, resolver, CancellationToken.None)).Should().BeTrue();
        (await PrinterCertificateNames.ConfiguredHostResolvesOnlyToLoopbackAsync(
             new PrusaConnectOptions(), resolver, CancellationToken.None)).Should().BeFalse("nothing is configured");
    }
}
