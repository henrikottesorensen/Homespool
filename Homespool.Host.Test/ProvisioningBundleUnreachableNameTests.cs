using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Homespool.Host.Certificates;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Transfers;

namespace Homespool.Host.Test;

/// <summary>
/// Which of a certificate's names are worth offering an operator - decided by resolving them, not by
/// looking at them.
/// </summary>
/// <remarks>
/// <para>
/// A Compose deployment issues its certificate over what it can see from inside the container, so the
/// leaf legitimately carries the container's own address and hostname beside the real one. Offering
/// those as equal choices is offering a bundle that cannot work: a printer is a physical device on the
/// household LAN and cannot route to <c>172.28.0.2</c> at all.
/// </para>
/// <para>
/// <b>Only a positive answer counts</b>, which is the half worth testing hardest. A name that does not
/// resolve stays on the list - a LAN name may be unresolvable from inside a container and perfectly
/// good from the printer's side - so nothing is dropped on the strength of a resolver having a bad day.
/// </para>
/// </remarks>
public sealed class ProvisioningBundleUnreachableNameTests : IDisposable
{
    private const string Token = "abcdefghijklmnopqrst";

    /// <summary>The network a Compose deployment pins, and tells the application about.</summary>
    private static readonly IReadOnlyList<IPNetwork> ComposeNetwork = [IPNetwork.Parse("172.16.0.0/12")];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hs-reach-{Guid.NewGuid():N}");

    private sealed class FakeResolver : IHostAddressResolver
    {
        private readonly Dictionary<string, IPAddress[]> _answers;

        public FakeResolver(Dictionary<string, IPAddress[]> answers)
        {
            _answers = answers;
        }

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string name, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<IPAddress>>(
                _answers.TryGetValue(name, out IPAddress[]? found) ? found : []);
        }
    }

    /// <summary>The rule itself, with no certificate and no network anywhere near it.</summary>
    [Theory]
    [InlineData(new[] { "172.28.0.2" }, true)]
    [InlineData(new[] { "172.17.0.5", "172.18.0.9" }, true)]
    [InlineData(new[] { "192.168.13.238" }, false)]
    [InlineData(new[] { "172.28.0.2", "192.168.13.238" }, false)]
    [InlineData(new string[0], false)]

    // The case that got through the first version: a resolver volunteering loopback or IPv6 alongside
    // the container address made "all of them are the container's" false, and the name survived.
    [InlineData(new[] { "172.28.0.2", "127.0.0.1" }, true)]
    [InlineData(new[] { "172.28.0.2", "::1" }, true)]
    [InlineData(new[] { "169.254.11.9" }, true)]
    public void ANameIsUnreachableOnlyWhenEverythingItResolvesToIsTheContainers(string[] resolved, bool expected)
    {
        IReadOnlyList<IPAddress> addresses = [.. resolved.Select(IPAddress.Parse)];

        ProvisioningBundleBuilder.IsUnreachableByPrinters(addresses, ComposeNetwork).Should().Be(expected);
    }

    /// <summary>
    /// The ranges come from configuration, so a deployment that pinned a different network is
    /// understood - and one whose real LAN uses 172.16/12 is not sabotaged.
    /// </summary>
    /// <remarks>
    /// <b>This is why it is a setting rather than a constant.</b> <c>compose.yaml</c> invites changing
    /// its subnet if it collides with something on the host, and a hardcoded 172.16/12 would then stop
    /// recognising the container's own address and go back to offering it - reopening the trap by way
    /// of a documented configuration change. The shipped stack feeds this from the same variable that
    /// pins the network, so the two cannot drift apart.
    /// </remarks>
    [Theory]
    [InlineData("10.10.0.0/16", "10.10.0.7", true)]
    [InlineData("10.10.0.0/16", "172.17.0.2", false)]
    [InlineData("172.16.0.0/12", "172.17.0.2", true)]
    public void TheRangesAreWhicheverTheDeploymentSaysTheyAre(string network, string address, bool unreachable)
    {
        IReadOnlyList<IPNetwork> configured = [IPNetwork.Parse(network)];

        ProvisioningBundleBuilder.IsUnreachableByPrinters([IPAddress.Parse(address)], configured)
            .Should().Be(unreachable);
    }

    /// <summary>
    /// An empty list means nothing is container-internal, which is the right answer for a deployment
    /// that is not in a container - including one whose LAN genuinely uses Docker's default range.
    /// </summary>
    [Fact]
    public void NoConfiguredRangesMeansNothingIsFiltered()
    {
        ProvisioningBundleBuilder.IsUnreachableByPrinters([IPAddress.Parse("172.17.0.2")], [])
            .Should().BeFalse();
    }

    /// <summary>
    /// The Compose case, end to end through the builder: the container's address and hostname both go,
    /// the LAN address stays.
    /// </summary>
    /// <remarks>
    /// The hostname is the half a heuristic could not have caught. <c>71e04654da9b</c> is a name like
    /// any other until you ask what it points at.
    /// </remarks>
    [Fact]
    public async Task TheContainersOwnAddressAndHostnameAreNotOfferedAsync()
    {
        // Arrange - exactly what a Compose deployment's leaf carried on 2026-07-30.
        PrinterCertificateAuthority authority = NewAuthority();
        authority.EnsureLeaf(["192.168.13.238", "71e04654da9b", "172.28.0.2"]).Dispose();

        FakeResolver resolver = new(new()
        {
            ["192.168.13.238"] = [IPAddress.Parse("192.168.13.238")],
            ["71e04654da9b"] = [IPAddress.Parse("172.28.0.2")],
            ["172.28.0.2"] = [IPAddress.Parse("172.28.0.2")],
        });

        // Act
        IReadOnlyList<PrinterAddressSuggestion> offered =
            await NewBuilder(authority, resolver).AvailableNamesAsync(CancellationToken.None);

        // Assert
        offered.Select(suggestion => suggestion.Value).Should().BeEquivalentTo(["192.168.13.238"],
            "a printer cannot route to a container's bridge network, so those are not choices - they are traps");
    }

    /// <summary>
    /// A name nothing can resolve is still offered, because "no answer" is not evidence against it.
    /// </summary>
    [Fact]
    public async Task AnUnresolvableNameIsStillOfferedAsync()
    {
        // Arrange - the resolver knows nothing at all, which is what a container's DNS often knows
        // about the LAN it sits on.
        PrinterCertificateAuthority authority = NewAuthority();
        authority.EnsureLeaf(["homespool.lan"]).Dispose();

        // Act
        IReadOnlyList<PrinterAddressSuggestion> offered = await NewBuilder(authority, new FakeResolver([]))
            .AvailableNamesAsync(CancellationToken.None);

        // Assert
        offered.Select(suggestion => suggestion.Value).Should().BeEquivalentTo(["homespool.lan"]);

        // And it says what choosing a name costs: it outlives a lease, if something resolves it.
        offered[0].Durability.Should().Be(AddressDurability.SurvivesALeaseChange);
        offered[0].Note.Should().Contain("router publishes names");
    }

    /// <summary>
    /// And one posted by hand is refused, since the check is the same list.
    /// </summary>
    [Fact]
    public async Task AContainerAddressPostedDirectlyIsRefusedAsync()
    {
        // Arrange
        PrinterCertificateAuthority authority = NewAuthority();
        authority.EnsureLeaf(["192.168.13.238", "172.28.0.2"]).Dispose();

        ProvisioningBundleBuilder builder = NewBuilder(authority, new FakeResolver(new()
        {
            ["192.168.13.238"] = [IPAddress.Parse("192.168.13.238")],
            ["172.28.0.2"] = [IPAddress.Parse("172.28.0.2")],
        }));

        // Act
        Func<Task> act = async () => await builder.BuildAsync("172.28.0.2", Token, "Bench printer", CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*resolves only inside this container*");
    }

    private static ProvisioningBundleBuilder NewBuilder(PrinterCertificateAuthority authority, IHostAddressResolver resolver)
    {
        return new(Options.Create(new PrusaConnectOptions { PrinterHost = "192.168.13.238", PrinterPort = 15443, PrinterTls = true }),
            Options.Create(new CertificateOptions { ContainerNetworks = ["172.16.0.0/12"] }),
            authority,
            resolver);
    }

    private PrinterCertificateAuthority NewAuthority()
    {
        return new(Options.Create(new CertificateOptions { Directory = "certs" }),
            new HostEnvironmentAccessor(_root),
            TimeProvider.System,
            NullLogger<PrinterCertificateAuthority>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
