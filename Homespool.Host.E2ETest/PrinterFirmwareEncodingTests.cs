using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Printing;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The firmware version a printer states about itself reaches two pages inside an
/// <c>Html.Raw</c> argument, and both must render it as text.
/// </summary>
/// <remarks>
/// <para>
/// <b>The version string is the printer's own claim, not ours</b> - it arrives in an <c>INFO</c>
/// event and is stored verbatim, so it is the one half of those two sentences that a person did not
/// write. Everything else interpolated into an <c>Html.Raw</c> in this application is a resource
/// string or a literal, which is why the encoding is easy to lose track of here specifically.
/// </para>
/// <para>
/// <b>Both payloads carry a <c>+</c>, and that is the point rather than decoration.</b> The gate in
/// front of each sink is <c>PrinterFirmwareVersion</c>, which discards build metadata after the
/// first <c>+</c> before parsing - so a string that is a valid version followed by anything at all
/// passes the gate and reaches the page whole. A payload without one would never get there, and a
/// test using one would pass while the sink stayed open.
/// </para>
/// <para>
/// <b>Asserting the encoded form is what makes these non-vacuous.</b> Neither alert renders at all
/// unless its gate opened, so finding the escaped text proves the payload arrived <i>and</i> that it
/// arrived as text; a test that only asserted the absence of markup would pass on a page that never
/// showed the warning.
/// </para>
/// </remarks>
public sealed class PrinterFirmwareEncodingTests : IAsyncLifetime
{
    /// <summary>A version the gate accepts, with markup riding on its build metadata.</summary>
    private const string HostileFirmware = "6.6.0+<img src=x onerror=alert(1)>";

    /// <summary>
    /// The payload's markup as it must reach the browser. The version digits are deliberately left
    /// out of this: the encoder is widened, so it escapes the <c>+</c> as well, and asserting on
    /// <c>&amp;#x2B;</c> would tie this test to that setting rather than to the encoding it is about.
    /// </summary>
    private const string Escaped = "&lt;img src=x onerror=alert(1)&gt;";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("firmware-encoding");

    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);

        // The bundle page offers the plaintext escape hatch only where the deployment has opened
        // one, so without these the legacy block - and the sink inside it - never renders.
        _factory.ConfigurationOverrides["PrusaConnect:LegacyPrinterPort"] = "15800";
        _factory.ConfigurationOverrides["PrusaConnect:PrinterTls"] = "true";

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _scratch.Dispose();
    }

    /// <summary>
    /// The printer page's plaintext warning names the firmware, and renders it as text.
    /// </summary>
    [Fact]
    public async Task ThePlaintextWarningEscapesTheStatedFirmware()
    {
        (Printer printer, HttpClient client) =
            await SeedAsync("firmware-detail@example.com", HostileFirmware);

        using (client)
        {
            // The warning is shown only for a printer connected over the legacy listener right now,
            // which the registry answers from the live connection rather than from a column.
            _factory.Services.GetRequiredService<PrinterConnectionRegistry>()
                    .Register(printer.Id, new OpenLink(), overPlaintext: true);

            using HttpResponseMessage response =
                await client.GetAsync($"/Printers/Detail/{printer.Uuid}", TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            html.Should().Contain(Escaped, "the warning has to name the version, and name it as text");
            html.Should().NotContain("<img src=x", "the printer must not be able to write markup into this page");
        }
    }

    /// <summary>
    /// The bundle page's argument against the plaintext listener names the firmware, and renders it
    /// as text.
    /// </summary>
    /// <remarks>
    /// Reached through the listing's reissue button rather than the Add page: the offer only carries
    /// a firmware string on a reissue, because that is the case where the printer has connected
    /// before and said what it runs. So the printer has to be provisioned the way an operator
    /// provisions one - there is a token to reissue only then - and told what it runs afterwards.
    /// </remarks>
    [Fact]
    public async Task TheBundleOffersArgumentEscapesTheStatedFirmware()
    {
        (Printer printer, HttpClient client) =
            await ProvisionAsync("firmware-bundle@example.com", HostileFirmware);

        using (client)
        {
            string listing =
                await (await client.GetAsync("/Printers", TestContext.Current.CancellationToken)).Content
                    .ReadAsStringAsync(TestContext.Current.CancellationToken);

            using FormUrlEncodedContent body = new(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(listing),
            });

            using HttpResponseMessage reissued = await client.PostAsync(
                $"/Printers?handler=Regenerate&printerId={printer.Id}", body, TestContext.Current.CancellationToken);

            reissued.StatusCode.Should().Be(HttpStatusCode.OK);

            string html = await reissued.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            html.Should().Contain(Escaped, "the argument against the legacy port has to name the version, as text");
            html.Should().NotContain("<img src=x", "the printer must not be able to write markup into this page");
        }
    }

    /// <summary>
    /// An account with one printer on its team, carrying the firmware string it last stated.
    /// </summary>
    private async Task<(Printer printer, HttpClient client)> SeedAsync(string email, string firmware)
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, email);

        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        TeamMember membership = await context.TeamMembers
                                             .SingleAsync(m => m.UserId == user.Id, TestContext.Current.CancellationToken);

        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            TeamId = membership.TeamId,
            Name = "Bench",
            Firmware = firmware,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (printer, client);
    }

    /// <summary>
    /// An account whose printer was provisioned through the Add page, so it has a USB token to
    /// reissue, and which has since stated <paramref name="firmware"/>.
    /// </summary>
    private async Task<(Printer printer, HttpClient client)> ProvisionAsync(string email, string firmware)
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, email);

        string addPage =
            await (await client.GetAsync("/Printers/Add", TestContext.Current.CancellationToken)).Content
                .ReadAsStringAsync(TestContext.Current.CancellationToken);

        using (FormUrlEncodedContent provisioning = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(addPage),
            ["Input.Name"] = "Bench",
            ["Input.Location"] = "Workshop",
        }))
        {
            using HttpResponseMessage provisioned =
                await client.PostAsync("/Printers/Add", provisioning, TestContext.Current.CancellationToken);

            provisioned.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        // Written to the column rather than sent as an INFO event: this test is about what the page
        // does with a stored string, and driving a whole connection to place one would make it a
        // test of the telemetry path instead.
        Printer printer = await context.Printers.SingleAsync(TestContext.Current.CancellationToken);
        printer.Firmware = firmware;

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (printer, client);
    }

    /// <summary>
    /// A connection that is up and does nothing else - all these tests ask of the registry is that
    /// it reports the printer as live on the plaintext listener.
    /// </summary>
    private sealed class OpenLink : IPrinterLink
    {
        public bool IsOpen => true;

        public Task<CommandSendResult> SendAsync(IPrinterIntent intent, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("these tests render a page and send the printer nothing");
        }

        public void Complete()
        {
        }
    }
}
