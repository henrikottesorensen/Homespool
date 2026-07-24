using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;

using NSubstitute;

using PrinterService.Data;
using PrinterService.Host.Exceptions;
using PrinterService.Host.PrusaConnect;
using PrinterService.Host.PrusaConnect.Commands;
using PrinterService.Host.Services;
using PrinterService.Model;
using PrinterService.Model.Entities;

namespace PrinterService.Host.Test;

/// <summary>
/// <see cref="PrinterCommandService"/> - the team-permission-checked entry point for sending a
/// command, and the first real consumer of <see cref="TeamMember.CanUse"/>.
/// </summary>
/// <remarks>
/// Run against real SQLite, matching <c>PrinterQueryServiceTests</c>.
/// </remarks>
public sealed class PrinterCommandServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-printercommand-{Guid.NewGuid():N}.db");

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

    private static async Task<Printer> AddPrinterAsync(PSDbContext context, int teamId)
    {
        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            Type = PrinterType.PrusaConnect,
            TeamId = teamId,
            Status = PrinterStatus.Unknown,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync();

        return printer;
    }

    /// <summary>
    /// Forces a deterministic outcome instead of driving a real connection and correlator - these
    /// tests are about the permission gate and the outcome-to-exception mapping, not the wire.
    /// </summary>
    private static IPrinterCommandTransport TransportReturning(CommandSendResult result)
    {
        IPrinterCommandTransport transport = Substitute.For<IPrinterCommandTransport>();
        transport.SendAsync(Arg.Any<int>(), Arg.Any<ISendableCommand>(), Arg.Any<CancellationToken>())
                 .Returns(result);

        return transport;
    }

    private static IPrinterCommandTransport TransportReturning(CommandSendOutcome outcome) =>
        TransportReturning(new CommandSendResult(outcome, null));

    [Fact]
    public async Task SendCommandAsyncReturnsTheOutcomeWhenTheCallerCanUse()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: false);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        IPrinterCommandTransport transport = TransportReturning(new CommandSendResult(CommandSendOutcome.Completed, new CommandOutcome(Events.Finished, null)));
        PrinterCommandService service = new(context, new TeamService(context), transport);
        PausePrint command = new();

        // Act
        CommandOutcome outcome = await service.SendCommandAsync(printer.Id, command, 1, CancellationToken.None);

        // Assert
        outcome.EventType.Should().Be(Events.Finished);
        await transport.Received(1).SendAsync(printer.Id, command, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendCommandAsyncThrowsAccessDeniedWhenTheCallerCanReadButNotUse()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: false, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        PrinterCommandService service = new(context, new TeamService(context), TransportReturning(CommandSendOutcome.Completed));

        // Act
        Func<Task> act = () => service.SendCommandAsync(printer.Id, new PausePrint(), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<TeamAccessDeniedException>();
    }

    [Fact]
    public async Task SendCommandAsyncThrowsAccessDeniedWhenTheCallerIsNotOnTheTeamAtAll()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();

        TeamMember someoneElses = await AddTeamAsync(context, userId: 2, canRead: true, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, someoneElses.TeamId);

        PrinterCommandService service = new(context, new TeamService(context), TransportReturning(CommandSendOutcome.Completed));

        // Act
        Func<Task> act = () => service.SendCommandAsync(printer.Id, new PausePrint(), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<TeamAccessDeniedException>();
    }

    [Fact]
    public async Task SendCommandAsyncThrowsPrinterNotFoundForAnUnknownId()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();

        PrinterCommandService service = new(context, new TeamService(context), TransportReturning(CommandSendOutcome.Completed));

        // Act
        Func<Task> act = () => service.SendCommandAsync(999, new PausePrint(), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<PrinterNotFoundException>();
    }

    [Fact]
    public async Task SendCommandAsyncThrowsNotConnectedWhenTheTransportReportsNotConnected()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        IPrinterCommandTransport transport = TransportReturning(CommandSendOutcome.NotConnected);
        PrinterCommandService service = new(context, new TeamService(context), transport);

        // Act
        Func<Task> act = () => service.SendCommandAsync(printer.Id, new PausePrint(), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<PrinterNotConnectedException>();
    }

    [Fact]
    public async Task SendCommandAsyncThrowsAlreadyInFlightWhenTheTransportReportsAlreadyInFlight()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        IPrinterCommandTransport transport = TransportReturning(CommandSendOutcome.AlreadyInFlight);
        PrinterCommandService service = new(context, new TeamService(context), transport);

        // Act
        Func<Task> act = () => service.SendCommandAsync(printer.Id, new PausePrint(), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<CommandAlreadyInFlightException>();
    }

    [Fact]
    public async Task SendCommandAsyncThrowsTimedOutWhenTheTransportReportsTimedOut()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canRead: true, canUse: true, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        IPrinterCommandTransport transport = TransportReturning(CommandSendOutcome.TimedOut);
        PrinterCommandService service = new(context, new TeamService(context), transport);

        // Act
        Func<Task> act = () => service.SendCommandAsync(printer.Id, new PausePrint(), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<CommandTimedOutException>();
    }
}
