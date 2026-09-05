using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.PrusaConnect;
using Homespool.Model.Entities;

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
public sealed class ProvisioningBundleDownloadTests : IAsyncLifetime
{
    private const string PrinterHost = HomespoolFactory.PrinterHost;

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("bundle-e2e");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);

        _ = _factory.Server;

        _factory.Services.GetRequiredService<Accounts.SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _scratch.Dispose();
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
            string addPage =
                await (await client.GetAsync("/Printers/Add", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                    TestContext.Current.CancellationToken);

            // Act - provision, which renders the download form with the one-time token in it.
            using FormUrlEncodedContent provisionForm = new(
            [
                new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(addPage)),
                new("Input.Name", "Bench printer"),
                new("Input.Location", "Workshop"),
            ]);

            using HttpResponseMessage provisioned =
                await client.PostAsync("/Printers/Add", provisionForm, TestContext.Current.CancellationToken);

            provisioned.StatusCode.Should().Be(HttpStatusCode.OK);

            string html = await provisioned.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
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

            using HttpResponseMessage download =
                await client.PostAsync("/Printers/Bundle", downloadForm, TestContext.Current.CancellationToken);

            // Assert
            download.StatusCode.Should().Be(HttpStatusCode.OK);
            download.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypeNames.Application.Zip);
            download.Content.Headers.ContentDisposition?.FileName.Should().Be("homespool-bench-printer.zip",
                                                                              "a downloads folder ends up holding several of these");

            Dictionary<string, byte[]> entries =
                Entries(await download.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

            entries.Keys.Should().BeEquivalentTo(["prusa_printer_settings.ini", "connect.der", "README.Bundle.md"]);

            // The instructions name this printer, because a downloads folder ends up holding several
            // and they are otherwise identical.
            Encoding.UTF8.GetString(entries["README.Bundle.md"]).Should().Contain("Bench printer");

            string ini = Encoding.UTF8.GetString(entries["prusa_printer_settings.ini"]);
            ini.Should().Contain($"hostname = {PrinterHost}").And.Contain("custom_cert = 1");

            string tokenInFile = ini.Split("token = ")[1].Split('\n')[0].Trim();
            tokenInFile.Should().Be(token);

            using IServiceScope scope = _factory.Services.CreateScope();
            Data.HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<Data.HomespoolDbContext>();
            PrusaConnectProvisioning stored = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                                                             .SingleAsync(context.PrusaConnectProvisionings,
                                                                          TestContext.Current.CancellationToken);

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
            string addPage =
                await (await client.GetAsync("/Printers/Add", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                    TestContext.Current.CancellationToken);

            // Act
            using FormUrlEncodedContent refusedForm = new(
            [
                new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(addPage)),
                new("Token", "irrelevant-but-well-formed"),
                new("Hostname", "192.0.2.77"),
                new("PrinterId", "1"),
            ]);

            using HttpResponseMessage refused =
                await client.PostAsync("/Printers/Bundle", refusedForm, TestContext.Current.CancellationToken);

            // Assert
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("does not cover");
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
