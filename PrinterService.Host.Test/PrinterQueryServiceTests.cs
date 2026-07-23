using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;

using PrinterService.Data;
using PrinterService.Host.Exceptions;
using PrinterService.Host.Services;
using PrinterService.Model;
using PrinterService.Model.Entities;

namespace PrinterService.Host.Test;

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

    private PSDbContext NewContext()
    {
        DbContextOptions<PSDbContext> options = new DbContextOptionsBuilder<PSDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        return new PSDbContext(options);
    }

    private async Task<PSDbContext> MigratedContextAsync()
    {
        PSDbContext context = NewContext();
        await context.Database.MigrateAsync();

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

    private static async Task<TeamMember> AddTeamAsync(PSDbContext context, long userId, bool canRead, bool canUse, bool canManage)
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
        await context.SaveChangesAsync();

        return team.Members.Single();
    }

    private static async Task<Printer> AddPrinterAsync(PSDbContext context, int teamId, string? name = null, string? location = null)
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
        await context.SaveChangesAsync();

        return printer;
    }

    // ---------- ListPrintersForUserAsync ----------

    /// <summary>Only printers on teams the caller has CanRead on come back.</summary>
    [Fact]
    public async Task ListPrintersForUserAsyncReturnsOnlyPrintersOnReadableTeams()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();

        TeamMember readable = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: true);
        Printer visible = await AddPrinterAsync(context, readable.TeamId);

        TeamMember unreadable = await AddTeamAsync(context, userId: 2, canRead: true, canUse: true, canManage: true);
        await AddPrinterAsync(context, unreadable.TeamId);

        // Act
        IReadOnlyList<Printer> printers = await new PrinterQueryService(context).ListPrintersForUserAsync(1, CancellationToken.None);

        // Assert
        printers.Select(p => p.Id).Should().ContainSingle().Which.Should().Be(visible.Id);
    }

    /// <summary>A membership with CanRead false doesn't surface that team's printers.</summary>
    [Fact]
    public async Task ListPrintersForUserAsyncExcludesTeamsWithoutCanRead()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();

        TeamMember noRead = await AddTeamAsync(context, userId: 1, canRead: false, canUse: true, canManage: true);
        await AddPrinterAsync(context, noRead.TeamId);

        // Act
        IReadOnlyList<Printer> printers = await new PrinterQueryService(context).ListPrintersForUserAsync(1, CancellationToken.None);

        // Assert
        printers.Should().BeEmpty();
    }

    // ---------- GetPrinterForUserAsync ----------

    /// <summary>A printer on a readable team is returned by its uuid.</summary>
    [Fact]
    public async Task GetPrinterForUserAsyncReturnsThePrinterWhenTheCallerCanReadItsTeam()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId, name: "MK4");

        // Act
        Printer? found = await new PrinterQueryService(context).GetPrinterForUserAsync(printer.Uuid, 1, CancellationToken.None);

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
        await using PSDbContext context = await MigratedContextAsync();

        TeamMember someoneElses = await AddTeamAsync(context, userId: 2, canRead: true, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, someoneElses.TeamId);

        // Act
        Printer? found = await new PrinterQueryService(context).GetPrinterForUserAsync(printer.Uuid, 1, CancellationToken.None);

        // Assert
        found.Should().BeNull();
    }

    /// <summary>An unknown uuid returns null rather than throwing.</summary>
    [Fact]
    public async Task GetPrinterForUserAsyncReturnsNullForAnUnknownUuid()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();

        // Act
        Printer? found = await new PrinterQueryService(context).GetPrinterForUserAsync(Guid.NewGuid(), 1, CancellationToken.None);

        // Assert
        found.Should().BeNull();
    }

    // ---------- UpdatePrinterAsync ----------

    /// <summary>A caller with CanManage can rename and relocate a printer on their team.</summary>
    [Fact]
    public async Task UpdatePrinterAsyncAppliesNameAndLocationWhenTheCallerCanManage()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId, name: "Old name", location: "Old location");

        // Act
        Printer? updated = await new PrinterQueryService(context)
            .UpdatePrinterAsync(printer.Uuid, 1, "New name", "New location", CancellationToken.None);

        // Assert
        updated.Should().NotBeNull();
        updated!.Name.Should().Be("New name");
        updated.Location.Should().Be("New location");

        Printer stored = await context.Printers.SingleAsync(p => p.Id == printer.Id);
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
        await using PSDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: false);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        // Act
        Func<Task> update = () => new PrinterQueryService(context)
            .UpdatePrinterAsync(printer.Uuid, 1, "New name", null, CancellationToken.None);

        // Assert
        await update.Should().ThrowAsync<TeamAccessDeniedException>();
    }

    /// <summary>
    /// A caller with no membership on the printer's team at all gets null, not the access-denied
    /// exception - the same "doesn't leak existence" rule <see cref="GetPrinterForUserAsync"/> follows.
    /// </summary>
    [Fact]
    public async Task UpdatePrinterAsyncReturnsNullWhenTheCallerIsNotOnItsTeam()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();

        TeamMember someoneElses = await AddTeamAsync(context, userId: 2, canRead: true, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, someoneElses.TeamId);

        // Act
        Printer? updated = await new PrinterQueryService(context)
            .UpdatePrinterAsync(printer.Uuid, 1, "New name", null, CancellationToken.None);

        // Assert
        updated.Should().BeNull();
    }

    /// <summary>An unknown uuid returns null rather than throwing.</summary>
    [Fact]
    public async Task UpdatePrinterAsyncReturnsNullForAnUnknownUuid()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();

        // Act
        Printer? updated = await new PrinterQueryService(context)
            .UpdatePrinterAsync(Guid.NewGuid(), 1, "New name", null, CancellationToken.None);

        // Assert
        updated.Should().BeNull();
    }

    /// <summary>Updating stamps UpdatedAt.</summary>
    [Fact]
    public async Task UpdatePrinterAsyncRefreshesUpdatedAt()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);
        printer.UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1);
        await context.SaveChangesAsync();

        DateTimeOffset before = DateTimeOffset.UtcNow;

        // Act
        Printer? updated = await new PrinterQueryService(context)
            .UpdatePrinterAsync(printer.Uuid, 1, null, null, CancellationToken.None);

        // Assert
        updated!.UpdatedAt.Should().BeOnOrAfter(before.AddSeconds(-1));
    }
}
