using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The front page's printer tiles, rendered the way a browser gets them.
/// </summary>
/// <remarks>
/// <para>
/// Razor compiles at request time, so a green build says nothing about whether this page renders.
/// These are what notice a broken partial, a handler no poll reaches, or a tile leaking a printer
/// the caller may not see.
/// </para>
/// <para>
/// <b>The polled handler is exercised as well as the page</b>, because the two render the same
/// partial by different routes and only one of them is covered by loading the page - which is
/// exactly the asymmetry that let a control strip vanish on refresh once
/// (<c>notes/printer-page.md</c> §6e).
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class FrontPageTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-frontpage-{Guid.NewGuid():N}.db");
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
        _factory?.Dispose();

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A visitor who is not signed in gets the holding page. There is nobody to have printers, so
    /// there are no tiles - and the page must still say something rather than render an empty grid.
    /// </summary>
    [Fact]
    public async Task ShowsTheHoldingPageToAVisitorWhoIsNotSignedIn()
    {
        // Act
        using HttpClient anonymous = _factory.CreateClient();
        string page = await (await anonymous.GetAsync("/", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        page.Should().Contain("Homespool");
        page.Should().NotContain("printer-shortcuts", "there is nobody whose printers these would be");
    }

    /// <summary>
    /// The tiles carry the printer's name and its status, and the most-used printer comes first.
    /// </summary>
    [Fact]
    public async Task OrdersTilesByHowMuchTheCallerHasUsedEachPrinter()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "front-order@example.com");

        (int busy, int quiet) = SeedPrinters(user.Id, "Busy One", "Quiet One");
        SeedJobs(busy, user.Id, count: 3);
        SeedJobs(quiet, user.Id, count: 1);

        // Act
        string page = await (await client.GetAsync("/", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        page.Should().Contain("Busy One");
        page.Should().Contain("Quiet One");
        page.IndexOf("Busy One", StringComparison.Ordinal)
            .Should().BeLessThan(page.IndexOf("Quiet One", StringComparison.Ordinal),
                                 "the printer you use most comes first");

        client.Dispose();
    }

    /// <summary>
    /// A printer that has reported a chamber gets the closed-box drawing; one that has not gets the
    /// open frame. This is the evidence-not-lookup rule reaching the page.
    /// </summary>
    [Fact]
    public async Task DrawsAnEnclosedPrinterAsABoxAndAnOpenOneAsAFrame()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "front-shape@example.com");

        (int enclosed, int open) = SeedPrinters(user.Id, "Boxed", "Framed");
        SeedLiveState(enclosed, chamber: 31.5f);
        SeedLiveState(open, chamber: null);

        // Act
        string page = await (await client.GetAsync("/", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        page.Should().Contain("printer-icon-enclosed");
        page.Should().Contain("printer-icon-open");

        client.Dispose();
    }

    /// <summary>
    /// <b>The poll renders the same tiles.</b> A handler that returned nothing, or that rendered a
    /// partial missing half its state, would leave a page that looks right until it refreshes itself.
    /// </summary>
    [Fact]
    public async Task ThePolledHandlerRendersTheSameTiles()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "front-poll@example.com");

        SeedPrinters(user.Id, "Polled One", "Polled Two");

        // Act
        string fragment = await (await client.GetAsync("/?handler=Tiles", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        fragment.Should().Contain("printer-shortcuts");
        fragment.Should().Contain("Polled One");
        fragment.Should().NotContain("<html", "the poll answers a fragment, not a whole page");

        client.Dispose();
    }

    /// <summary>
    /// <b>A disconnected printer shows its filament but not its progress.</b> The live state persists,
    /// so every reading on it outlives the connection; progress and the time left describe a print
    /// nobody can watch and are dropped, while what filament is loaded stays true with the power off.
    /// </summary>
    /// <remarks>
    /// This is the one rule on the tile that a reader would most reasonably assume the other way, and
    /// the failure it prevents is a page contradicting itself - "42%" above an <i>Offline</i> badge.
    /// </remarks>
    [Fact]
    public async Task DropsProgressForADisconnectedPrinterButKeepsItsFilament()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "front-stale@example.com");

        (int printer, _) = SeedPrinters(user.Id, "Switched Off", "Spare");
        SeedLiveState(printer, chamber: null, progress: 42, timeRemaining: 4320, material: "PETG");

        // Act
        string page = await (await client.GetAsync("/", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        page.Should().Contain("PETG", "what is loaded stays true while the power is off");
        page.Should().NotContain("42%", "progress on a printer nobody can reach is a frozen reading");
        page.Should().NotContain("printer-plaque-progress", "and the bar goes with it");

        client.Dispose();
    }

    /// <summary>
    /// Tiles are scoped to the caller. Somebody else's printer is not yours to see, and a front page
    /// is a careless place to leak the shape of a rack.
    /// </summary>
    [Fact]
    public async Task DoesNotShowAPrinterTheCallerCannotSee()
    {
        // Arrange
        (HSUser alice, HttpClient aliceClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "front-alice@example.com");
        (HSUser bob, HttpClient bobClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "front-bob@example.com");

        SeedPrinters(alice.Id, "Alice Only", "Alice Spare");
        SeedPrinters(bob.Id, "Bob Only", "Bob Spare");

        // Act
        string page = await (await aliceClient.GetAsync("/", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        page.Should().Contain("Alice Only");
        page.Should().NotContain("Bob Only", "the front page is scoped to the signed-in user");

        aliceClient.Dispose();
        bobClient.Dispose();
    }

    /// <summary>
    /// Two printers on the team registration already made for this user, so the access path under
    /// test is the real one rather than a membership invented here.
    /// </summary>
    private (int first, int second) SeedPrinters(long userId, string firstName, string secondName)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        int teamId = context.TeamMembers.First(member => member.UserId == userId).TeamId;

        Printer first = new() { Uuid = Guid.NewGuid(), TeamId = teamId, Name = firstName };
        Printer second = new() { Uuid = Guid.NewGuid(), TeamId = teamId, Name = secondName };
        context.Printers.AddRange(first, second);
        context.SaveChanges();

        return (first.Id, second.Id);
    }

    private void SeedJobs(int printerId, long userId, int count)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        for (int i = 0; i < count; i++)
        {
            context.PrintJobs.Add(new PrintJob
            {
                TrackingId = Guid.NewGuid(),
                PrinterId = printerId,
                FileName = $"part-{i}.bgcode",
                QueuedByUserId = userId,
                StartedAt = DateTimeOffset.UtcNow.AddDays(-1),
                EndedAt = DateTimeOffset.UtcNow.AddHours(-1),
            });
        }

        context.SaveChanges();
    }

    private void SeedLiveState(int printerId,
                               float? chamber,
                               int? progress = null,
                               int? timeRemaining = null,
                               string? material = null)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        context.PrinterLiveStates.Add(new PrinterLiveState
        {
            PrinterId = printerId,
            NozzleTemperature = 25,
            ChamberTemperature = chamber,
            Progress = progress,
            TimeRemaining = timeRemaining,
            Material = material,
        });

        context.SaveChanges();
    }
}
