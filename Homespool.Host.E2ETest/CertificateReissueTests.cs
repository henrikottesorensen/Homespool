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
    /// Reissuing replaces the leaf, keeps the authority, and says that a restart is needed.
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
        after.Should().ContainEquivalentOf("restart the server",
            "the running process still serves the old certificate, and a page that only said \"done\" would be "
            + "describing a file rather than what printers meet");
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
