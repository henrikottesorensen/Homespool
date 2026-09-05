using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Homespool.Host.Certificates;
using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

/// <summary>
/// Drift detection: whether the printer certificate still matches the machine it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// The leaf is issued once and frozen, so this is the half of that decision that keeps it payable - a
/// moved lease leaves a certificate nobody can verify, and every printer simply stops connecting
/// without reporting anything to anyone.
/// </para>
/// <para>
/// The descriptions are asserted as well as the statuses, because <c>HealthBanner</c> shows them
/// verbatim to an administrator. A check that said "Degraded" and nothing usable would be a banner
/// nobody can act on.
/// </para>
/// </remarks>
public sealed class PrinterCertificateHealthCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hs-drift-{Guid.NewGuid():N}");

    private static Task<HealthCheckResult> RunAsync(PrinterCertificateHealthCheck check)
    {
        return check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
    }

    private static PrinterCertificateHealthCheck NewCheck(PrinterCertificateAuthority authority, string host, bool tls = true)
    {
        return new(authority,
                   TestOptions.Monitor(new PrusaConnectOptions { PrinterHost = host, PrinterTls = tls }),
                   Options.Create(new CertificateOptions()),
                   new DnsHostAddressResolver(),
                   TimeProvider.System);
    }

    private PrinterCertificateAuthority NewAuthority(int authorityDays = 5475, int leafDays = 730)
    {
        return new(Options.Create(new CertificateOptions
                   {
                       Directory = "certs",
                       AuthorityPassphrase = "unit test passphrase",
                       AuthorityValidityDays = authorityDays,
                       LeafValidityDays = leafDays,
                   }),
                   new HostEnvironmentAccessor(_root),
                   TimeProvider.System,
                   NullLogger<PrinterCertificateAuthority>.Instance,
                   new PrinterLeafChangeToken());
    }

    /// <summary>
    /// A certificate covering the address printers are told to use is healthy, whatever else this
    /// machine happens to have.
    /// </summary>
    /// <remarks>
    /// Extra addresses are ordinary - a VPN, a container bridge, a second interface - and treating
    /// them as drift would put a permanent banner on every developer's machine, which is how a warning
    /// stops being read.
    /// </remarks>
    [Fact]
    public async Task ACertificateCoveringTheConfiguredAddressIsHealthy()
    {
        // Arrange
        PrinterCertificateAuthority authority = NewAuthority();
        authority.EnsureLeaf(["printers.example.com"]);

        // Act
        HealthCheckResult result = await RunAsync(NewCheck(authority, "printers.example.com"));

        // Assert
        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("printers.example.com");
    }

    /// <summary>
    /// The drift that stops provisioning outright: the certificate does not cover the address every
    /// bundle would be written for.
    /// </summary>
    [Fact]
    public async Task AConfiguredAddressOutsideTheCertificateIsDegraded()
    {
        // Arrange - issued for the old address, configuration has moved on.
        PrinterCertificateAuthority authority = NewAuthority();
        authority.EnsureLeaf(["192.168.13.238"]);

        // Act
        HealthCheckResult result = await RunAsync(NewCheck(authority, "192.168.13.99"));

        // Assert
        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("192.168.13.99")
              .And.Contain("192.168.13.238", "the message has to say what it does cover, or there is nothing to act on")
              .And.Contain("Admin -> Printer certificate");
    }

    /// <summary>
    /// A leaf near its expiry is worth saying so about, since replacing it costs a restart and
    /// nothing at the printers.
    /// </summary>
    [Fact]
    public async Task ALeafCloseToExpiryIsDegraded()
    {
        // Arrange - five days of validity, against a thirty-day warning.
        PrinterCertificateAuthority authority = NewAuthority(leafDays: 5);
        authority.EnsureLeaf(["printers.example.com"]);

        // Act
        HealthCheckResult result = await RunAsync(NewCheck(authority, "printers.example.com"));

        // Assert
        result.Status.Should().Be(HealthStatus.Degraded);

        // "proxy reload", not "restart": the proxy holds the leaf now, and telling an operator to
        // restart the application would be both wrong and needlessly expensive.
        result.Description.Should().Contain("expires on").And.Contain("proxy reload");
    }

    /// <summary>
    /// The authority nearing expiry is the expensive one, and the message has to say why.
    /// </summary>
    /// <remarks>
    /// A <c>.der</c> cannot be delivered over Connect, so replacing the authority means a USB visit to
    /// every printer and none of them can connect until visited. A year's notice is the first time
    /// anybody will have thought about it since the deployment was built.
    /// </remarks>
    [Fact]
    public async Task AnAuthorityCloseToExpiryIsDegradedAndSaysWhatItCosts()
    {
        // Arrange - a hundred days of authority, against a year's warning; the leaf outlives it here,
        // which is fine because the leaf's own expiry is checked first and passes.
        PrinterCertificateAuthority authority = NewAuthority(authorityDays: 100, leafDays: 90);
        authority.EnsureLeaf(["printers.example.com"]);

        // Act
        HealthCheckResult result = await RunAsync(NewCheck(authority, "printers.example.com"));

        // Assert
        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("AUTHORITY")
              .And.Contain("USB visit to every printer");
    }

    /// <summary>
    /// With no certificate issued at all, nothing can verify this server and the check says so.
    /// </summary>
    [Fact]
    public async Task NoCertificateAtAllIsDegraded()
    {
        // Act
        HealthCheckResult result = await RunAsync(NewCheck(NewAuthority(), "printers.example.com"));

        // Assert
        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("No printer certificate has been issued");
    }

    /// <summary>
    /// A plaintext deployment has no certificate to drift, and is not nagged about one.
    /// </summary>
    /// <remarks>
    /// Startup already warns, loudly, every time. A permanent banner saying the same thing is how a
    /// warning becomes wallpaper.
    /// </remarks>
    [Fact]
    public async Task PlaintextDeploymentsAreHealthyAndNotNagged()
    {
        // Act
        HealthCheckResult result = await RunAsync(NewCheck(NewAuthority(), "192.168.13.238", tls: false));

        // Assert
        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("plain HTTP");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
