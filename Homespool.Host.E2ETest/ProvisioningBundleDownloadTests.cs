using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using AwesomeAssertions;
using Homespool.Host.Certificates;
using Homespool.Host.PrusaConnect;
using Homespool.Model.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The provisioning bundle, driven the way an operator drives it: sign in, provision a printer on the
/// Add page, then press the download button on the page that comes back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both requests matter, and the second is the point.</b> The token is PBKDF2-hashed at rest, so
/// the download cannot be a link the server resolves later — the page posts the token back and the zip
/// is assembled around it. That is the flow this exercises end to end; the unit tests cover what is
/// inside the file.
/// </para>
/// <para>
/// It also proves the token that reaches the zip is the one the printer will authenticate with,
/// by verifying it against the stored hash — the property that made the old on-screen snippet correct,
/// carried over to a file the operator never reads.
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class ProvisioningBundleDownloadTests : IAsyncLifetime, IDisposable
{
    private const string PrinterHost = "printers.example.com";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-bundle-e2e-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");

        _ = _factory.Server;

        // Provisioning offers the names the printer certificate covers, and nothing issues one in a
        // test host - no listener is ever bound. So the certificate this deployment would have minted
        // at startup is minted here instead, for the address the test configuration advertises.
        using IServiceScope scope = _factory.Services.CreateScope();
        PrinterCertificateAuthority authority = scope.ServiceProvider.GetRequiredService<PrinterCertificateAuthority>();
        authority.EnsureLeaf([PrinterHost]);

        _factory.Services.GetRequiredService<Services.SetupState>().MarkComplete();

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
    /// Provision, then download: the response is a zip carrying the ini and the anchor, and the token
    /// inside it is the one the printer will present.
    /// </summary>
    [Fact]
    public async Task ProvisioningThenDownloadingYieldsAZipCarryingTheRealToken()
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "bundles@example.com");

        using (client)
        {
            string addPage = await (await client.GetAsync("/Printers/Add")).Content.ReadAsStringAsync();

            // Act - provision, which renders the download form with the one-time token in it.
            using FormUrlEncodedContent provisionForm = new(
            [
                new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(addPage)),
                new("Input.Name", "Bench printer"),
                new("Input.Location", "Workshop"),
            ]);

            using HttpResponseMessage provisioned = await client.PostAsync("/Printers/Add", provisionForm);

            provisioned.StatusCode.Should().Be(HttpStatusCode.OK);

            string html = await provisioned.Content.ReadAsStringAsync();
            string token = HiddenFieldValue(html, "Token");

            html.Should().Contain(PrinterHost, "the address the certificate covers is offered as the default");

            // Act - press the download button.
            using FormUrlEncodedContent downloadForm = new(
            [
                new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(html)),
                new("Token", token),
                new("Hostname", PrinterHost),
                new("PrinterId", HiddenFieldValue(html, "PrinterId")),
                new("PrinterName", "Bench printer"),
            ]);

            using HttpResponseMessage download = await client.PostAsync("/Printers/Bundle", downloadForm);

            // Assert
            download.StatusCode.Should().Be(HttpStatusCode.OK);
            download.Content.Headers.ContentType?.MediaType.Should().Be("application/zip");
            download.Content.Headers.ContentDisposition?.FileName.Should().Be("homespool-bench-printer.zip",
                "a downloads folder ends up holding several of these");

            Dictionary<string, byte[]> entries = Entries(await download.Content.ReadAsByteArrayAsync());

            entries.Keys.Should().BeEquivalentTo(["prusa_printer_settings.ini", "connect.der"]);

            string ini = Encoding.UTF8.GetString(entries["prusa_printer_settings.ini"]);
            ini.Should().Contain($"hostname = {PrinterHost}").And.Contain("custom_cert = 1");

            string tokenInFile = ini.Split("token = ")[1].Split('\n')[0].Trim();
            tokenInFile.Should().Be(token);

            using IServiceScope scope = _factory.Services.CreateScope();
            Data.HSDbContext context = scope.ServiceProvider.GetRequiredService<Data.HSDbContext>();
            PrusaConnectProvisioning stored = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .SingleAsync(context.PrusaConnectProvisionings);

            new TokenService().VerifyToken(tokenInFile, stored.HashedToken).Should().BeTrue(
                "the file has to carry the token the printer will authenticate with, not a copy of something else");
        }
    }

    /// <summary>
    /// A name the certificate does not cover is refused, even though the deployment can reach it.
    /// </summary>
    /// <remarks>
    /// The leaf is frozen at first issue, so "this machine answers on that address" and "a printer can
    /// verify that address" are different questions. Refusing here moves the failure off the printer's
    /// screen, where it arrives days later as a bare TLS error, and onto the page of whoever is
    /// provisioning.
    /// </remarks>
    [Fact]
    public async Task ABundleForAnAddressTheCertificateDoesNotCoverIsRefused()
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "wrong-name@example.com");

        using (client)
        {
            string addPage = await (await client.GetAsync("/Printers/Add")).Content.ReadAsStringAsync();

            // Act
            using FormUrlEncodedContent refusedForm = new(
            [
                new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(addPage)),
                new("Token", "irrelevant-but-well-formed"),
                new("Hostname", "192.0.2.77"),
                new("PrinterId", "1"),
            ]);

            using HttpResponseMessage refused = await client.PostAsync("/Printers/Bundle", refusedForm);

            // Assert
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await refused.Content.ReadAsStringAsync()).Should().Contain("does not cover");
        }
    }

    private static string HiddenFieldValue(string html, string name)
    {
        Match match = Regex.Match(html, $"""<input[^>]*name="{name}"[^>]*value="([^"]*)"[^>]*>""");
        match.Success.Should().BeTrue($"the download form must carry {name}");

        return match.Groups[1].Value;
    }

    private static Dictionary<string, byte[]> Entries(byte[] zip)
    {
        using MemoryStream stream = new(zip);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);

        return archive.Entries.ToDictionary(
            entry => entry.FullName,
            entry =>
            {
                using Stream content = entry.Open();
                using MemoryStream buffer = new();
                content.CopyTo(buffer);

                return buffer.ToArray();
            },
            StringComparer.Ordinal);
    }
}
