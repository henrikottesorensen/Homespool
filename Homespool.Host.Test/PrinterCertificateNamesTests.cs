using System;
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

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>(
                _answers.TryGetValue(name, out IPAddress[]? found) ? found : []);
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
}
