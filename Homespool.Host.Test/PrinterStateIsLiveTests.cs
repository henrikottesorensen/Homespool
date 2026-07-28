using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;
using Homespool.Data;
using Homespool.Host.PrusaConnect.DTO.App;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace Homespool.Host.Test;

/// <summary>
/// The state a printer reports over the app API comes from its live state, not from the
/// <see cref="Printer"/> row.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the test that was missing.</b> <see cref="Printer.Status"/> is assigned once, at
/// creation, as <see cref="PrinterStatus.Unknown"/>, and nothing has ever updated it; telemetry
/// writes <see cref="PrinterLiveState.Status"/> on a different entity. So <c>GET /api/v1/printers</c>
/// reported <c>UNKNOWN</c> for every printer forever, however busy it was - and every existing test
/// agreed with it, because they all seeded printers that had never connected.
/// </para>
/// <para>
/// Which is the shape of the trap: a field that is always the same wrong value looks exactly like a
/// field that is correctly reporting a fleet of idle printers. These tests seed the two entities in
/// <em>disagreement</em>, which is the only arrangement that can tell them apart.
/// </para>
/// </remarks>
public sealed class PrinterStateIsLiveTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-livestate-{Guid.NewGuid():N}.db");

    private HSDbContext NewContext()
    {
        DbContextOptions<HSDbContext> options = new DbContextOptionsBuilder<HSDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        return new HSDbContext(options);
    }

    private async Task<HSDbContext> MigratedContextAsync()
    {
        HSDbContext context = NewContext();
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

    private static async Task<Printer> AddPrinterAsync(HSDbContext context, long userId, PrinterStatus? liveStatus)
    {
        Team team = new() { CreatedBy = userId, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(team);
        await context.SaveChangesAsync();

        context.TeamMembers.Add(new TeamMember
        {
            TeamId = team.Id,
            UserId = userId,
            CanRead = true,
            CanUse = true,
            CanManage = true,
            IsDefault = true,
        });

        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            Type = PrinterType.PrusaConnect,
            TeamId = team.Id,
            Name = "Bench printer",

            // Left at Unknown deliberately: this is the value the row is created with and keeps
            // forever, so a reader that consults it cannot be distinguished from a broken one unless
            // the live state below disagrees.
            Status = PrinterStatus.Unknown,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync();

        if (liveStatus is { } status)
        {
            context.PrinterLiveStates.Add(new PrinterLiveState
            {
                PrinterId = printer.Id,
                Status = status,
                LastSeenAt = DateTimeOffset.UtcNow,
            });

            await context.SaveChangesAsync();
        }

        return printer;
    }

    /// <summary>A printer that telemetry says is printing is reported as printing, not as unknown.</summary>
    [Fact]
    public async Task ListReportsTheLiveStateRatherThanThePrinterRow()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        await AddPrinterAsync(context, userId: 1, liveStatus: PrinterStatus.Printing);

        // Act
        IReadOnlyList<PrinterWithState> listed = await new PrinterQueryService(context)
            .ListPrintersWithStateForUserAsync(1, CancellationToken.None);

        // Assert
        listed.Should().ContainSingle();
        listed[0].LiveState.Should().NotBeNull();

        PrinterReadDTO dto = PrinterReadDTO.FromEntity(listed[0]);

        dto.State.Should().Be(PrinterStatus.Printing.ToConnectState());
        dto.State.Should().NotBe(PrinterStatus.Unknown.ToConnectState(), "the Printer row still says Unknown");
    }

    /// <summary>Same for the single-printer read, which is the one a caller uses after finding a uuid.</summary>
    [Fact]
    public async Task GetReportsTheLiveStateRatherThanThePrinterRow()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, userId: 1, liveStatus: PrinterStatus.Paused);

        // Act
        PrinterWithState? found = await new PrinterQueryService(context)
            .GetPrinterWithStateForUserAsync(printer.Uuid, 1, CancellationToken.None);

        // Assert
        found.Should().NotBeNull();
        PrinterReadDTO.FromEntity(found!).State.Should().Be(PrinterStatus.Paused.ToConnectState());
    }

    /// <summary>
    /// A printer that has never connected has no live state at all, and <c>UNKNOWN</c> is then the
    /// true answer rather than a placeholder - which is what the original phase-1.5 wording meant.
    /// </summary>
    [Fact]
    public async Task APrinterThatHasNeverConnectedIsStillUnknown()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        await AddPrinterAsync(context, userId: 1, liveStatus: null);

        // Act
        IReadOnlyList<PrinterWithState> listed = await new PrinterQueryService(context)
            .ListPrintersWithStateForUserAsync(1, CancellationToken.None);

        // Assert
        listed.Should().ContainSingle();
        listed[0].LiveState.Should().BeNull();
        PrinterReadDTO.FromEntity(listed[0]).State.Should().Be(PrinterStatus.Unknown.ToConnectState());
    }

    /// <summary>
    /// Everything the printer tells us about itself reaches the API shape.
    /// </summary>
    /// <remarks>
    /// A near-tautological mapping test, and worth it for exactly the reason this file exists: the
    /// failure being guarded against is not a wrong value but an <em>absent</em> one. <c>Model</c> was
    /// written to the database and missing from this DTO for months without anything noticing, because
    /// nothing asserted it was there.
    /// </remarks>
    [Fact]
    public async Task TheDtoCarriesWhatTheInfoEventTaught()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, userId: 1, liveStatus: PrinterStatus.Idle);

        printer.Model = "1.3.5";
        printer.SerialNumber = "SN-12345";
        printer.Firmware = "6.5.7";
        await context.SaveChangesAsync();

        // Act
        PrinterWithState? found = await new PrinterQueryService(context)
            .GetPrinterWithStateForUserAsync(printer.Uuid, 1, CancellationToken.None);

        // Assert
        PrinterReadDTO dto = PrinterReadDTO.FromEntity(found!);

        dto.PrinterModel.Should().Be("1.3.5");
        dto.SerialNumber.Should().Be("SN-12345");
        dto.Firmware.Should().Be("6.5.7");
    }

    /// <summary>
    /// An edit reports the same state the next read will. A PATCH answering <c>UNKNOWN</c> while a GET
    /// a second later says <c>PRINTING</c> would read as the edit having reset something.
    /// </summary>
    [Fact]
    public async Task PatchReportsTheSameStateAsGet()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, userId: 1, liveStatus: PrinterStatus.Printing);

        // Act
        PrinterWithState? updated = await new PrinterQueryService(context)
            .UpdatePrinterAsync(printer.Uuid, 1, "Renamed", "Garage", CancellationToken.None);

        // Assert
        updated.Should().NotBeNull();
        PrinterReadDTO.FromEntity(updated!).State.Should().Be(PrinterStatus.Printing.ToConnectState());
    }
}
