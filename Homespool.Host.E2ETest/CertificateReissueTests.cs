using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

using AwesomeAssertions;
using Homespool.Host.Certificates;
using Homespool.Host.Services;
using Homespool.Model.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The reissue an administrator reaches for once drift has been reported: it replaces the certificate
/// and touches nothing at any printer.
/// </summary>
/// <remarks>
/// This is the other half of "the leaf is issued once and then frozen". The freezing is what keeps
/// live connections up and the certificate predictable; this is what stops that being a trap when the
/// machine's addresses move.
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class CertificateReissueTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-reissue-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");

        _ = _factory.Server;

        _factory.Services.GetRequiredService<SetupState>().MarkComplete();

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Dispose();

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _factory.Dispose();

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Reissuing replaces the leaf, keeps the authority, and says that a proxy reload is needed.
    /// </summary>
    /// <remarks>
    /// <b>The authority is the assertion that matters.</b> If a reissue rolled the CA, every printer
    /// already provisioned would stop trusting this server and need a USB visit - turning a one-click
    /// fix into a fleet-wide outage. Printers trust the authority precisely so the leaf can be
    /// replaced without them.
    /// </remarks>
    [Fact]
    public async Task ReissuingReplacesTheLeafAndKeepsTheAuthority()
    {
        // Arrange
        PrinterCertificateAuthority authority = _factory.Services.GetRequiredService<PrinterCertificateAuthority>();
        using X509Certificate2 originalAuthority = authority.EnsureAuthority();

        // IssueLeaf, not EnsureLeaf: the host already has one from startup, and the case under test is
        // a certificate issued long ago for an address this machine has since stopped having.
        using X509Certificate2 originalLeaf = authority.IssueLeaf(["an-old-address.lan"]);

        using HttpClient client = await AdministratorClientAsync();

        string page = await (await client.GetAsync("/Admin/Certificate")).Content.ReadAsStringAsync();
        page.Should().Contain("an-old-address.lan", "the page has to show what the certificate covers today");

        // Act
        using FormUrlEncodedContent form = new(
        [
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
        ]);

        using HttpResponseMessage response = await client.PostAsync("/Admin/Certificate?handler=Reissue", form);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using X509Certificate2? reissued = authority.LoadLeafIfIssued();

        reissued.Should().NotBeNull();
        reissued!.Thumbprint.Should().NotBe(originalLeaf.Thumbprint, "a reissue must produce a new certificate");

        authority.EnsureAuthority().Thumbprint.Should().Be(originalAuthority.Thumbprint,
            "rolling the authority would strand every printer already provisioned");

        string after = await (await client.GetAsync("/Admin/Certificate")).Content.ReadAsStringAsync();
        after.Should().ContainEquivalentOf("reload the proxy",
            "the proxy still serves the old certificate, and a page that only said \"done\" would be describing a "
            + "file rather than what printers meet");
        after.Should().NotContainEquivalentOf("restart the server",
            "that was the instruction while Kestrel held the leaf; it is now both wrong and needlessly expensive, "
            + "since reloading nginx keeps the application and every user session up");
    }

    /// <summary>
    /// A reissue that would narrow the certificate says so before the button is pressed.
    /// </summary>
    /// <remarks>
    /// <b>This is the one way the reissue button can break a printer that was working.</b> Names are
    /// filtered at issuance to what a printer could actually reach, so a name detected once and no
    /// longer resolvable is dropped — correct in itself, since it vouches for nothing. But a printer
    /// whose ini still names it keeps dialling it, and after the reissue and the proxy reload its
    /// handshake fails against a leaf that no longer covers it. mbedTLS reports that as a bare TLS
    /// error naming neither the name nor the certificate, and putting the printer back means a USB
    /// visit. The page has always shown drift in the other direction — names this machine has that the
    /// certificate lacks — and was silent about this one.
    /// </remarks>
    [Fact]
    public async Task ThePageWarnsWhenAReissueWouldDropNamesTheCertificateCovers()
    {
        // Arrange - a leaf covering a name this machine cannot answer on, which is exactly what a
        // moved DHCP lease or a renamed host leaves behind.
        PrinterCertificateAuthority authority = _factory.Services.GetRequiredService<PrinterCertificateAuthority>();
        using X509Certificate2 stale = authority.IssueLeaf(["an-old-address.lan"]);

        using HttpClient client = await AdministratorClientAsync();

        // Act
        string page = await (await client.GetAsync("/Admin/Certificate")).Content.ReadAsStringAsync();

        // Assert
        page.Should().ContainEquivalentOf("would narrow this certificate",
            "an operator about to drop a name their printers may be dialling has to be told before pressing, "
            + "not after a printer stops connecting");
        page.Should().Contain("an-old-address.lan", "and the warning has to name which names go");
        page.Should().ContainEquivalentOf("USB visit",
            "the cost of getting it wrong is what makes the warning worth reading");
    }

    /// <summary>
    /// An ordinary user cannot see the page, let alone press the button.
    /// </summary>
    [Fact]
    public async Task ThePageIsAdministratorsOnly()
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "ordinary@example.com");

        using (client)
        {
            // Act
            using HttpResponseMessage response = await client.GetAsync("/Admin/Certificate");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Redirect);
            response.Headers.Location?.ToString().Should().Contain("AccessDenied");
        }
    }

    private async Task<HttpClient> AdministratorClientAsync()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "admin@example.com", AdminBootstrap.AdminRole);

        return client;
    }
}
