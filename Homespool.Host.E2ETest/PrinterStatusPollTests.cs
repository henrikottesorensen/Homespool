using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The two handlers behind the printer page's self-refreshing blocks.
/// </summary>
/// <remarks>
/// <para>
/// <b>These answer with a fragment, not a page, and that is the thing to hold down.</b> The script
/// puts whatever comes back straight into the card, so a handler that quietly started returning a
/// whole layout - or a sign-in page - would fill the status card with a copy of the site rather than
/// fail in any visible way.
/// </para>
/// <para>
/// <b>Which is why the anonymous case is here too.</b> An unauthenticated fetch that redirected to
/// the login form would answer 200 with a login page, and the poll would paste it into the printer
/// page every two seconds.
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class PrinterStatusPollTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-status-poll-{Guid.NewGuid():N}.db");

    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();

        return ValueTask.CompletedTask;
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
    /// The page carries the two regions and the script that drives them, so the poll is wired by what
    /// is served rather than by anything a test has to assume.
    /// </summary>
    [Fact]
    public async Task ThePageDeclaresItsLiveRegions()
    {
        (Guid uuid, HttpClient client) = await SeedAsync("status-regions@example.com");

        using (client)
        {
            string page = await GetAsync(client, $"/Printers/Detail/{uuid}");

            page.Should().Contain("data-live-region");
            page.Should().Contain($"handler=Status");
            page.Should().Contain($"handler=Graph");

            // Matched without the extension: asp-append-version rewrites the *filename* rather than
            // adding a query string, so the served tag reads "live-region.ebg0z79z8q.js".
            page.Should().Contain("/js/live-region.");
        }
    }

    /// <summary>
    /// The status handler answers with the card alone. Asserted by what is <em>absent</em>: no
    /// document, and none of the page chrome the layout would bring with it.
    /// </summary>
    [Fact]
    public async Task TheStatusHandlerAnswersAFragment()
    {
        (Guid uuid, HttpClient client) = await SeedAsync("status-fragment@example.com");

        using (client)
        {
            string fragment = await GetAsync(client, $"/Printers/Detail/{uuid}?handler=Status");

            fragment.Should().Contain("printer-status", "it is the status card");
            fragment.Should().NotContain("<!DOCTYPE", "a fragment is not a document");
            fragment.Should().NotContain("<nav", "the layout would bring the navbar with it");
        }
    }

    /// <summary>
    /// The queue handler answers the queue and nothing else.
    /// </summary>
    /// <remarks>
    /// <b>The boundary is the point, and it was drawn in the wrong place once.</b> A polled partial
    /// may only render state its own handler loads. The queue partial briefly carried the slicer
    /// address and the remote-ready switch, which come from <c>SlicerUrl</c> and <c>CanManage</c> -
    /// set on the full page load and by no poll - so every queue refresh blanked the address and took
    /// the switch away until somebody reloaded. Widening this partial again brings that back.
    /// </remarks>
    [Fact]
    public async Task TheQueueHandlerAnswersOnlyTheQueue()
    {
        (Guid uuid, HttpClient client) = await SeedAsync("queue-fragment@example.com");

        using (client)
        {
            string fragment = await GetAsync(client, $"/Printers/Detail/{uuid}?handler=Queue");

            fragment.Should().NotContain("<!DOCTYPE", "a fragment is not a document");
            fragment.Should().NotContain("handler=RemoteReady",
                                         "the ready switch is rendered from CanManage, which no poll sets");
            fragment.Should().NotContain("compat/octoprint",
                                         "the slicer address is rendered from SlicerUrl, which no poll sets");
        }
    }

    /// <summary>
    /// A printer with live state says what it is doing, in words, rather than as an enum member.
    /// </summary>
    [Fact]
    public async Task TheCardReportsWhatThePrinterIsDoing()
    {
        (Guid uuid, HttpClient client) = await SeedAsync("status-words@example.com", state: new PrinterLiveState
        {
            Status = PrinterStatus.Printing,
            Progress = 64,
            TimePrinting = 7680,
            TimeRemaining = 4320,
            NozzleTemperature = 215,
            TargetNozzleTemperature = 215,
            BedTemperature = 54,
            TargetBedTemperature = 60,
            LastSeenAt = DateTimeOffset.UtcNow,
        });

        using (client)
        {
            string fragment = await GetAsync(client, $"/Printers/Detail/{uuid}?handler=Status");

            fragment.Should().Contain("Printing");
            fragment.Should().Contain("64", "the progress figure is on the card");
            fragment.Should().Contain("at target", "the nozzle has arrived");
            fragment.Should().Contain("heating to", "and the bed has not");
        }
    }

    /// <summary>The graph handler answers a fragment too, and the fragment is the drawing.</summary>
    [Fact]
    public async Task TheGraphHandlerAnswersAnSvg()
    {
        (Guid uuid, HttpClient client) = await SeedAsync("status-graph@example.com", state: new PrinterLiveState
        {
            Status = PrinterStatus.Printing,
            LastSeenAt = DateTimeOffset.UtcNow,
        }, samples: 300);

        using (client)
        {
            string fragment = await GetAsync(client, $"/Printers/Detail/{uuid}?handler=Graph");

            fragment.Should().Contain("<svg");
            fragment.Should().Contain("printer-graph-nozzle");
            fragment.Should().NotContain("<!DOCTYPE");
        }
    }

    /// <summary>
    /// A printer that has reported nothing gets a sentence rather than an empty pair of axes.
    /// </summary>
    [Fact]
    public async Task AGraphWithNoReadingsSaysSo()
    {
        (Guid uuid, HttpClient client) = await SeedAsync("status-graph-empty@example.com");

        using (client)
        {
            string fragment = await GetAsync(client, $"/Printers/Detail/{uuid}?handler=Graph");

            fragment.Should().NotContain("<svg");
            fragment.Should().Contain("no temperatures");
        }
    }

    /// <summary>
    /// Signed out, the poll is refused rather than answered with a login page it would then paste
    /// into the card.
    /// </summary>
    [Fact]
    public async Task AnAnonymousPollIsNotGivenALoginPage()
    {
        (Guid uuid, HttpClient client) = await SeedAsync("status-anon@example.com");

        client.Dispose();

        using HttpClient anonymous = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using HttpResponseMessage response = await anonymous.GetAsync($"/Printers/Detail/{uuid}?handler=Status",
                                                                      TestContext.Current.CancellationToken);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.Unauthorized);

        if (response.Content.Headers.ContentLength is > 0)
        {
            string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            body.Should().NotContain("printer-status", "an unauthenticated caller is never handed the card");
        }
    }

    /// <summary>A uuid the caller cannot read is a 404 here, as everywhere else on this page.</summary>
    [Fact]
    public async Task AnUnknownPrinterIsNotFound()
    {
        (Guid _, HttpClient client) = await SeedAsync("status-unknown@example.com");

        using (client)
        {
            using HttpResponseMessage response = await client.GetAsync(
                $"/Printers/Detail/{Guid.NewGuid()}?handler=Status", TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    private static async Task<string> GetAsync(HttpClient client, string url)
    {
        using HttpResponseMessage response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private async Task<(Guid uuid, HttpClient client)> SeedAsync(string email,
                                                                 PrinterLiveState? state = null,
                                                                 int samples = 0)
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, email);

        Guid uuid = Guid.NewGuid();

        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        TeamMember membership = await context.TeamMembers
                                             .SingleAsync(m => m.UserId == user.Id, TestContext.Current.CancellationToken);

        Printer printer = new()
        {
            Uuid = uuid,
            TeamId = membership.TeamId,
            Name = "Garage MK3.5",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        if (state is not null)
        {
            state.PrinterId = printer.Id;
            context.PrinterLiveStates.Add(state);
        }

        for (int second = 0; second < samples; second++)
        {
            context.TelemetrySamples.Add(new TelemetrySample
            {
                PrinterId = printer.Id,
                Timestamp = DateTimeOffset.UtcNow.AddSeconds(-samples + second),
                Status = PrinterStatus.Printing,
                NozzleTemperature = 200 + (second / 100f),
                BedTemperature = 60,
                TargetNozzleTemperature = 215,
                TargetBedTemperature = 60,
            });
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (uuid, client);
    }
}
