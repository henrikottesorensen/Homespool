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
            // Act - provision, which renders the download form with the one-time token in it.
            string html = await ProvisionAsync(client);
            string token = HiddenFieldValue(html, "Token");

            html.Should().Contain(PrinterHost, "the address the certificate covers is offered as the default");

            // Act - press the download button.
            using FormUrlEncodedContent downloadForm = new(
            [
                new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(html)),
                new("Token", token),
                new("Hostname", PrinterHost),
                new("PrinterId", HiddenFieldValue(html, "PrinterId")),
            ]);

            using HttpResponseMessage download =
                await client.PostAsync("/Printers/Bundle", downloadForm, TestContext.Current.CancellationToken);

            // Assert
            download.StatusCode.Should().Be(HttpStatusCode.OK);
            download.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypeNames.Application.Zip);

            // The POST carries a token, an address and an id - no name. So a file named after this
            // printer is proof the name was read from its row rather than taken from the form.
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
            // A real printer of this caller's own: the address is what is on trial here, so the
            // permission check ahead of it has to be satisfied rather than tripped.
            string html = await ProvisionAsync(client);

            // Act
            using FormUrlEncodedContent refusedForm = new(
            [
                new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(html)),
                new("Token", "irrelevant-but-well-formed"),
                new("Hostname", "192.0.2.77"),
                new("PrinterId", HiddenFieldValue(html, "PrinterId")),
            ]);

            using HttpResponseMessage refused =
                await client.PostAsync("/Printers/Bundle", refusedForm, TestContext.Current.CancellationToken);

            // Assert
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("does not cover");
        }
    }

    /// <summary>
    /// Another account's printer id is refused, however well-formed the rest of the POST is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The token is the caller's to supply, so what is on trial here is the id beside it.</b>
    /// Nothing secret is handed over when this succeeds - the bundle would carry the stranger's own
    /// made-up token and be useless to any printer - but everything downstream names the printer
    /// they chose, and one of those things is the warning that records whose traffic crosses the
    /// network in clear. An unchecked id lets any account write that line about somebody else's
    /// machine, which is a log that lies in both directions at once.
    /// </para>
    /// <para>
    /// Answered <c>404</c> rather than <c>403</c>: this page renders nothing and has nowhere to say
    /// more, and a refusal that told "no such printer" from "not yours" apart would be an
    /// enumeration oracle for the price of a POST.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APrinterBelongingToAnotherAccountIsRefused()
    {
        // Arrange - one account with a printer, and a stranger with an account of their own.
        (HSUser _, HttpClient owner) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "owner@example.com");
        (HSUser _, HttpClient stranger) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "stranger@example.com");

        using (owner)
        using (stranger)
        {
            string ownersPrinterId = HiddenFieldValue(await ProvisionAsync(owner, "Owner's printer"), "PrinterId");

            string strangersAddPage =
                await (await stranger.GetAsync("/Printers/Add", TestContext.Current.CancellationToken)).Content
                    .ReadAsStringAsync(TestContext.Current.CancellationToken);

            // Act
            using FormUrlEncodedContent form = new(
            [
                new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(strangersAddPage)),
                new("Token", "a-token-of-my-own-invention"),
                new("Hostname", PrinterHost),
                new("PrinterId", ownersPrinterId),
            ]);

            using HttpResponseMessage refused =
                await stranger.PostAsync("/Printers/Bundle", form, TestContext.Current.CancellationToken);

            // Assert
            refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
            refused.Content.Headers.ContentType?.MediaType.Should().NotBe(MediaTypeNames.Application.Zip);
        }
    }

    /// <summary>
    /// The first half of the flow: provision a printer, and hand back the page carrying its one-time
    /// token and its id.
    /// </summary>
    private static async Task<string> ProvisionAsync(HttpClient client, string name = "Bench printer")
    {
        string addPage =
            await (await client.GetAsync("/Printers/Add", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);

        using FormUrlEncodedContent provisionForm = new(
        [
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(addPage)),
            new("Input.Name", name),
            new("Input.Location", "Workshop"),
        ]);

        using HttpResponseMessage provisioned =
            await client.PostAsync("/Printers/Add", provisionForm, TestContext.Current.CancellationToken);

        provisioned.StatusCode.Should().Be(HttpStatusCode.OK);

        return await provisioned.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
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
