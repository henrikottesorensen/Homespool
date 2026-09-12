using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The printers listing, rendered the way a browser gets it: one card per printer, in the front
/// page's tile vocabulary.
/// </summary>
/// <remarks>
/// Razor compiles at request time, so a green build says nothing about whether this page renders.
/// The handlers it posts to are covered elsewhere - the default-printer and firmware-encoding
/// suites drive them - so these are about what the cards say.
/// </remarks>
public sealed class PrintersPageTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("printers-page");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _factory?.Dispose();

        _scratch.Dispose();
    }

    /// <summary>
    /// Every printer gets a card with its name, where it is and whose it is - and a printer that has
    /// reported a chamber gets the closed-box drawing while one that has not gets the open frame.
    /// </summary>
    [Fact]
    public async Task ListsEveryPrinterAsACardWithItsDrawing()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "printers-cards@example.com");

        (int enclosed, int open) = SeedPrinters(user.Id, "Boxed", "Framed", location: "Workshop");
        SeedLiveState(enclosed, chamber: 31.5f);
        SeedLiveState(open, chamber: null);

        // Act
        string page = await GetListingAsync(client);

        // Assert
        page.Should().Contain("printer-rack");
        page.Should().Contain("Boxed");
        page.Should().Contain("Framed");
        page.Should().Contain("Workshop &middot;", "where it is sits on the card, beside whose it is");
        page.Should().Contain("printer-icon-enclosed");
        page.Should().Contain("printer-icon-open");

        client.Dispose();
    }

    /// <summary>
    /// <b>A disconnected printer shows its filament but not its progress</b> - the same rule the front
    /// page's tiles follow, for the same reason: the live state persists, so a percentage on a printer
    /// nobody can reach is a frozen reading, while what filament is loaded stays true with the power
    /// off.
    /// </summary>
    [Fact]
    public async Task DropsProgressForADisconnectedPrinterButKeepsItsFilament()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "printers-stale@example.com");

        (int printer, _) = SeedPrinters(user.Id, "Switched Off", "Spare");
        SeedLiveState(printer, chamber: null, progress: 42, timeRemaining: 4320, material: "PETG");

        // Act
        string page = await GetListingAsync(client);

        // Assert
        page.Should().Contain("PETG", "what is loaded stays true while the power is off");
        page.Should().NotContain("42%", "progress on a printer nobody can reach is a frozen reading");
        page.Should().NotContain("printer-plaque-progress", "and the bar goes with it");

        client.Dispose();
    }

    /// <summary>
    /// <b>The listing no longer drives the printer.</b> Pause, resume and stop live on the printer's
    /// own page, beside the status they act on; a card here offers only the two things a tile has no
    /// room for.
    /// </summary>
    [Fact]
    public async Task OffersNoPrintControls()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "printers-controls@example.com");

        SeedPrinters(user.Id, "Quiet", "Quieter");

        // Act
        string page = await GetListingAsync(client);

        // Assert
        page.Should().Contain("handler=Default", "making one the default is what the listing is for");
        page.Should().NotContain("handler=Pause");
        page.Should().NotContain("handler=Resume");
        page.Should().NotContain("handler=Stop");

        client.Dispose();
    }

    private static async Task<string> GetListingAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/Printers", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Two printers on the team registration already made for this user, so the access path under
    /// test is the real one rather than a membership invented here.
    /// </summary>
    private (int first, int second) SeedPrinters(long userId, string firstName, string secondName, string? location = null)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        int teamId = context.TeamMembers.First(member => member.UserId == userId).TeamId;

        Printer first = new() { Uuid = Guid.NewGuid(), TeamId = teamId, Name = firstName, Location = location };
        Printer second = new() { Uuid = Guid.NewGuid(), TeamId = teamId, Name = secondName, Location = location };
        context.Printers.AddRange(first, second);
        context.SaveChanges();

        return (first.Id, second.Id);
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
