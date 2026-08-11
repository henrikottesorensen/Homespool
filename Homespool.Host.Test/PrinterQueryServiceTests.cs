using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrinterQueryService"/> - team-permission-checked reads and edits of a printer for the
/// app-facing <c>GET/PATCH /api/v1/printers</c> surface (AGENT-NOTES phase-1.5 §15 step 7b).
/// </summary>
/// <remarks>
/// Run against real SQLite rather than the in-memory provider, matching the other phase-1.5 service
/// tests in this project.
/// </remarks>
public sealed class PrinterQueryServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-printerquery-{Guid.NewGuid():N}.db");

    private HomespoolDbContext NewContext()
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={_databasePath}")
                                                       .Options;

        return new HomespoolDbContext(options);
    }

    private async Task<HomespoolDbContext> MigratedContextAsync()
    {
        HomespoolDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        return context;
    }

    public void Dispose()
    {
        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task<TeamMember> AddTeamAsync(HomespoolDbContext context,
                                                       long userId,
                                                       bool canRead,
                                                       bool canUse,
                                                       bool canManage)
    {
        Team team = new()
        {
            CreatedBy = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            Members =
            {
                new TeamMember
                {
                    UserId = userId,
                    CanRead = canRead,
                    CanUse = canUse,
                    CanManage = canManage,
                    IsDefault = true,
                },
            },
        };

        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return team.Members.Single();
    }

    private static async Task<Printer> AddPrinterAsync(HomespoolDbContext context,
                                                       int teamId,
                                                       string? name = null,
                                                       string? location = null)
    {
        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            Type = PrinterType.PrusaConnect,
            TeamId = teamId,
            Name = name,
            Location = location,
            Status = PrinterStatus.Unknown,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return printer;
    }

    // ---------- ListPrintersForUserAsync ----------

    /// <summary>Only printers on teams the caller has CanRead on come back.</summary>
    [Fact]
    public async Task ListPrintersForUserAsyncReturnsOnlyPrintersOnReadableTeams()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember readable = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: true);
        Printer visible = await AddPrinterAsync(context, readable.TeamId);

        TeamMember unreadable = await AddTeamAsync(context, userId: 2, canRead: true, canUse: true, canManage: true);
        await AddPrinterAsync(context, unreadable.TeamId);

        // Act
        IReadOnlyList<Printer> printers =
            await new PrinterQueryService(context, new PrinterAccessService(context), TimeProvider.System).ListPrintersForUserAsync(
                1, CancellationToken.None);

        // Assert
        printers.Select(p => p.Id).Should().ContainSingle().Which.Should().Be(visible.Id);
    }

    /// <summary>A membership with CanRead false doesn't surface that team's printers.</summary>
    [Fact]
    public async Task ListPrintersForUserAsyncExcludesTeamsWithoutCanRead()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember noRead = await AddTeamAsync(context, userId: 1, canRead: false, canUse: true, canManage: true);
        await AddPrinterAsync(context, noRead.TeamId);

        // Act
        IReadOnlyList<Printer> printers =
            await new PrinterQueryService(context, new PrinterAccessService(context), TimeProvider.System).ListPrintersForUserAsync(
                1, CancellationToken.None);

        // Assert
        printers.Should().BeEmpty();
    }

    // ---------- GetPrinterForUserAsync ----------

    /// <summary>A printer on a readable team is returned by its uuid.</summary>
    [Fact]
    public async Task GetPrinterForUserAsyncReturnsThePrinterWhenTheCallerCanReadItsTeam()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId, name: "MK4");

        // Act
        Printer? found =
            await new PrinterQueryService(context, new PrinterAccessService(context), TimeProvider.System).GetPrinterForUserAsync(
                printer.Uuid, 1, CancellationToken.None);

        // Assert
        found.Should().NotBeNull();
        found!.Name.Should().Be("MK4");
    }

    /// <summary>
    /// A printer on a team the caller isn't a member of comes back null - identical to an unknown
    /// uuid, so a 404 never confirms the printer exists on someone else's team.
    /// </summary>
    [Fact]
    public async Task GetPrinterForUserAsyncReturnsNullWhenTheCallerIsNotOnItsTeam()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember someoneElses = await AddTeamAsync(context, userId: 2, canRead: true, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, someoneElses.TeamId);

        // Act
        Printer? found =
            await new PrinterQueryService(context, new PrinterAccessService(context), TimeProvider.System).GetPrinterForUserAsync(
                printer.Uuid, 1, CancellationToken.None);

        // Assert
        found.Should().BeNull();
    }

    /// <summary>An unknown uuid returns null rather than throwing.</summary>
    [Fact]
    public async Task GetPrinterForUserAsyncReturnsNullForAnUnknownUuid()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        // Act
        Printer? found =
            await new PrinterQueryService(context, new PrinterAccessService(context), TimeProvider.System).GetPrinterForUserAsync(
                Guid.NewGuid(), 1, CancellationToken.None);

        // Assert
        found.Should().BeNull();
    }

    // ---------- UpdatePrinterAsync ----------

    /// <summary>A caller with CanManage can rename and relocate a printer on their team.</summary>
    [Fact]
    public async Task UpdatePrinterAsyncAppliesNameAndLocationWhenTheCallerCanManage()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId, name: "Old name", location: "Old location");

        // Act
        PrinterWithState? updated = await new PrinterQueryService(context, new PrinterAccessService(context), TimeProvider.System)
            .UpdatePrinterAsync(printer.Uuid, 1, "New name", "New location", CancellationToken.None);

        // Assert
        updated.Should().NotBeNull();
        updated!.Printer.Name.Should().Be("New name");
        updated.Printer.Location.Should().Be("New location");

        Printer stored = await context.Printers.SingleAsync(p => p.Id == printer.Id, TestContext.Current.CancellationToken);
        stored.Name.Should().Be("New name");
        stored.Location.Should().Be("New location");
    }

    /// <summary>
    /// A caller who can read but not manage the team is rejected with the access-denied exception,
    /// not treated as if the printer didn't exist - reaching this branch already proves they can see it.
    /// </summary>
    [Fact]
    public async Task UpdatePrinterAsyncThrowsWhenTheCallerCanReadButNotManage()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: false);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        // Act
        Func<Task> update = () => new PrinterQueryService(context, new PrinterAccessService(context), TimeProvider.System)
            .UpdatePrinterAsync(printer.Uuid, 1, "New name", null, CancellationToken.None);

        // Assert
        await update.Should().ThrowAsync<TeamAccessDeniedException>();
    }

    /// <summary>
    /// A caller with no membership on the printer's team at all gets null, not the access-denied
    /// exception - the same "doesn't leak existence" rule
    /// <see cref="PrinterQueryService.GetPrinterForUserAsync"/> follows.
    /// </summary>
    [Fact]
    public async Task UpdatePrinterAsyncReturnsNullWhenTheCallerIsNotOnItsTeam()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember someoneElses = await AddTeamAsync(context, userId: 2, canRead: true, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, someoneElses.TeamId);

        // Act
        PrinterWithState? updated = await new PrinterQueryService(context, new PrinterAccessService(context), TimeProvider.System)
            .UpdatePrinterAsync(printer.Uuid, 1, "New name", null, CancellationToken.None);

        // Assert
        updated.Should().BeNull();
    }

    /// <summary>An unknown uuid returns null rather than throwing.</summary>
    [Fact]
    public async Task UpdatePrinterAsyncReturnsNullForAnUnknownUuid()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        // Act
        PrinterWithState? updated = await new PrinterQueryService(context, new PrinterAccessService(context), TimeProvider.System)
            .UpdatePrinterAsync(Guid.NewGuid(), 1, "New name", null, CancellationToken.None);

        // Assert
        updated.Should().BeNull();
    }

    /// <summary>Updating stamps UpdatedAt.</summary>
    [Fact]
    public async Task UpdatePrinterAsyncRefreshesUpdatedAt()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);
        printer.UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        DateTimeOffset before = DateTimeOffset.UtcNow;

        // Act
        PrinterWithState? updated = await new PrinterQueryService(context, new PrinterAccessService(context), TimeProvider.System)
            .UpdatePrinterAsync(printer.Uuid, 1, null, null, CancellationToken.None);

        // Assert
        updated!.Printer.UpdatedAt.Should().BeOnOrAfter(before.AddSeconds(-1));
    }

    // ---------- GetPrinterStatisticsForUserAsync ----------

    /// <summary>Unknown uuid returns null, same as GetPrinterForUserAsync.</summary>
    [Fact]
    public async Task GetPrinterStatisticsForUserAsyncReturnsNullForAnUnknownUuid()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        // Act
        PrinterStatistics? statistics =
            await new PrinterQueryService(context, new PrinterAccessService(context), TimeProvider.System)
                .GetPrinterStatisticsForUserAsync(Guid.NewGuid(), 1, CancellationToken.None);

        // Assert
        statistics.Should().BeNull();
    }

    /// <summary>A printer on a team the caller isn't a member of returns null - the same
    /// "doesn't leak existence" rule GetPrinterForUserAsync follows.</summary>
    [Fact]
    public async Task GetPrinterStatisticsForUserAsyncReturnsNullWhenTheCallerIsNotOnItsTeam()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember someoneElses = await AddTeamAsync(context, userId: 2, canRead: true, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, someoneElses.TeamId);

        // Act
        PrinterStatistics? statistics =
            await new PrinterQueryService(context, new PrinterAccessService(context), TimeProvider.System)
                .GetPrinterStatisticsForUserAsync(printer.Uuid, 1, CancellationToken.None);

        // Assert
        statistics.Should().BeNull();
    }

    /// <summary>A membership with CanRead false is treated the same as no membership at all.</summary>
    [Fact]
    public async Task GetPrinterStatisticsForUserAsyncReturnsNullWithoutCanRead()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember noRead = await AddTeamAsync(context, userId: 1, canRead: false, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, noRead.TeamId);

        // Act
        PrinterStatistics? statistics =
            await new PrinterQueryService(context, new PrinterAccessService(context), TimeProvider.System)
                .GetPrinterStatisticsForUserAsync(printer.Uuid, 1, CancellationToken.None);

        // Assert
        statistics.Should().BeNull();
    }

    /// <summary>A printer with no telemetry yet returns a null live state and empty history,
    /// not an exception.</summary>
    [Fact]
    public async Task GetPrinterStatisticsForUserAsyncReturnsEmptyHistoryForAPrinterWithNoTelemetryYet()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        // Act
        PrinterStatistics? statistics =
            await new PrinterQueryService(context, new PrinterAccessService(context), TimeProvider.System)
                .GetPrinterStatisticsForUserAsync(printer.Uuid, 1, CancellationToken.None);

        // Assert
        statistics.Should().NotBeNull();
        statistics!.LiveState.Should().BeNull();
        statistics.RecentSamples.Should().BeEmpty();
        statistics.RecentEvents.Should().BeEmpty();
    }

    /// <summary>Live state, samples and events are all returned, samples and events newest first.</summary>
    [Fact]
    public async Task GetPrinterStatisticsForUserAsyncReturnsLiveStateAndHistoryNewestFirst()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        context.PrinterLiveStates.Add(new PrinterLiveState
        {
            PrinterId = printer.Id,
            LastSeenAt = DateTimeOffset.UtcNow,
            Status = PrinterStatus.Printing,
            Progress = 42,
        });

        DateTimeOffset now = DateTimeOffset.UtcNow;
        context.TelemetrySamples.AddRange(
            new TelemetrySample
                { PrinterId = printer.Id, Timestamp = now.AddMinutes(-2), Status = PrinterStatus.Printing, Progress = 40 },
            new TelemetrySample { PrinterId = printer.Id, Timestamp = now, Status = PrinterStatus.Printing, Progress = 42 });
        context.PrinterEvents.AddRange(
            new PrinterEvent
            {
                PrinterId = printer.Id, Timestamp = now.AddMinutes(-1), EventType = Events.Info, Status = PrinterStatus.Printing
            },
            new PrinterEvent
            {
                PrinterId = printer.Id, Timestamp = now, EventType = Events.Finished, Status = PrinterStatus.Printing,
                Reason = "No print to pause"
            });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        PrinterStatistics? statistics =
            await new PrinterQueryService(context, new PrinterAccessService(context), TimeProvider.System)
                .GetPrinterStatisticsForUserAsync(printer.Uuid, 1, CancellationToken.None);

        // Assert
        statistics.Should().NotBeNull();
        statistics!.LiveState.Should().NotBeNull();
        statistics.LiveState!.Progress.Should().Be(42);

        statistics.RecentSamples.Should().HaveCount(2);
        statistics.RecentSamples[0].Progress.Should().Be(42, "newest first");
        statistics.RecentSamples[1].Progress.Should().Be(40);

        statistics.RecentEvents.Should().HaveCount(2);
        statistics.RecentEvents[0].EventType.Should().Be(Events.Finished, "newest first");
        statistics.RecentEvents[0].Reason.Should().Be("No print to pause");
        statistics.RecentEvents[1].EventType.Should().Be(Events.Info);
    }

    /// <summary>More rows exist than the display cap - only the newest RecentSampleCount survive.</summary>
    [Fact]
    public async Task GetPrinterStatisticsForUserAsyncCapsTheSampleCountAtFifty()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 60; i++)
        {
            context.TelemetrySamples.Add(new TelemetrySample
            {
                PrinterId = printer.Id, Timestamp = now.AddSeconds(-i), Status = PrinterStatus.Printing, Progress = i
            });
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        PrinterStatistics? statistics =
            await new PrinterQueryService(context, new PrinterAccessService(context), TimeProvider.System)
                .GetPrinterStatisticsForUserAsync(printer.Uuid, 1, CancellationToken.None);

        // Assert
        statistics!.RecentSamples.Should().HaveCount(50);
        statistics.RecentSamples[0].Progress.Should().Be(0, "the most recent (i=0, latest timestamp) sample must survive the cap");
    }
}
