using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Host.Firmware;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Transfers;
using Homespool.Host.Queue;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrinterPreheatService"/>'s gate: heating and cooling are <c>ControlPrinter</c>, and a
/// key holding that alone reaches the printer.
/// </summary>
/// <remarks>
/// The command service is real and the connection is a substitute actor, so a send that got past
/// every check is seen arriving - with the temperatures it carried - rather than inferred from the
/// absence of a refusal.
/// </remarks>
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                 Justification = "TestTelemetryContext.For builds a second context over the same SQLite file the " +
                                 "test already owns and deletes in Dispose. EF opens and closes the connection per " +
                                 "query, so nothing is held between them.")]
public sealed class PrinterPreheatServiceTests : IDisposable
{
    private const int PrinterId = 1;

    private const long Owner = 1;

    private static readonly FilamentPreset Pla = new("PLA", NozzleTemperature: 215, BedTemperature: 60);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"hs-preheat-{Guid.NewGuid():N}.db");

    private readonly PrinterConnectionRegistry _registry = new(TimeProvider.System, NullLogger<PrinterConnectionRegistry>.Instance);

    private readonly IPrinterConnectionActor _actor = Substitute.For<IPrinterConnectionActor>();

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

    [Fact]
    public async Task AKeyHoldingControlPrinterPreheatsBothHeaters()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await SeedAsync(context, CapabilityPresets.Operator);

        // Act
        PreheatOutcome outcome = await NewService(context).PreheatAsync(PrinterId, TestCallers.Scoped(Owner, Capability.ControlPrinter),
                                                                        Pla, TestContext.Current.CancellationToken);

        // Assert
        outcome.Answer.Should().NotBeNull();

        SetTemperatures sent = SentTemperatures();
        sent.NozzleTemperature.Should().Be(215);
        sent.BedTemperature.Should().Be(60);
    }

    [Fact]
    public async Task AKeyHoldingControlPrinterCoolsBothHeatersToZero()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await SeedAsync(context, CapabilityPresets.Operator);

        // Act
        PreheatOutcome outcome = await NewService(context).CooldownAsync(PrinterId, TestCallers.Scoped(Owner, Capability.ControlPrinter),
                                                                         TestContext.Current.CancellationToken);

        // Assert
        outcome.Answer.Should().NotBeNull();

        SetTemperatures sent = SentTemperatures();
        sent.NozzleTemperature.Should().Be(0);
        sent.BedTemperature.Should().Be(0);
    }

    /// <summary>
    /// Heating somebody's printer is running the machine, so a key that may only print is refused by
    /// the key, naming what a replacement needs - and nothing reaches the printer.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AKeyThatMayOnlyPrintIsRefusedNamingControlPrinter(bool heating)
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await SeedAsync(context, CapabilityPresets.Operator);
        PrinterPreheatService service = NewService(context);
        Caller printing = TestCallers.Scoped(Owner, Capability.Print);

        // Act
        Func<Task> act = heating ?
            () => service.PreheatAsync(PrinterId, printing, Pla, TestContext.Current.CancellationToken) :
            () => service.CooldownAsync(PrinterId, printing, TestContext.Current.CancellationToken);

        // Assert
        (await act.Should().ThrowAsync<CredentialScopeDeniedException>()).Which.Missing.Should().Be(Capability.ControlPrinter);
        _actor.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name == nameof(IPrinterConnectionActor.SendAsync));
    }

    /// <summary>The team half: a member who may only print is refused by the team.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AMemberWhoMayOnlyPrintIsRefusedByTheTeam(bool heating)
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        await SeedAsync(context, CapabilityPresets.Contributor);
        PrinterPreheatService service = NewService(context);
        Caller member = Caller.Unscoped(Owner);

        // Act
        Func<Task> act = heating ?
            () => service.PreheatAsync(PrinterId, member, Pla, TestContext.Current.CancellationToken) :
            () => service.CooldownAsync(PrinterId, member, TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<TeamAccessDeniedException>();
        _actor.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name == nameof(IPrinterConnectionActor.SendAsync));
    }

    private SetTemperatures SentTemperatures()
    {
        return _actor.ReceivedCalls()
                     .Where(call => call.GetMethodInfo().Name == nameof(IPrinterConnectionActor.SendAsync))
                     .Select(call => call.GetArguments()[0])
                     .OfType<SetTemperatures>()
                     .Should().ContainSingle().Subject;
    }

    private PrinterPreheatService NewService(HomespoolDbContext context)
    {
        PrinterAccessService access = new(context, NullLogger<PrinterAccessService>.Instance);

        return new PrinterPreheatService(new PrinterCommandService(access, _registry),
                                         access,
                                         new QueueSnapshotReader(context, TestTelemetryContext.For(context), _registry, TimeProvider.System, access, Substitute.For<ITransferOffers>(), Substitute.For<IFirmwareInstallations>()),
                                         new ToolTargetReader(context, TestTelemetryContext.For(context)));
    }

    /// <summary>
    /// One idle, connected, single-tool printer the owner holds <paramref name="membership"/> on - so
    /// a caller allowed through the gate meets no other refusal, and reaches the printer.
    /// </summary>
    private async Task SeedAsync(HomespoolDbContext context, IReadOnlyList<Capability> membership)
    {
        Team team = new() { Name = "team" };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        TestAccounts.Add(context, Owner);
        context.TeamMembers.Add(TestMemberships.With(team.Id, Owner, [.. membership]));

        context.Printers.Add(new Printer
        {
            Id = PrinterId,
            Uuid = Guid.NewGuid(),
            Type = PrinterType.PrusaConnect,
            TeamId = team.Id,
            Status = PrinterStatus.Unknown,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        // Connected before the state below is reported, so it counts as said to this connection.
        _actor.IsOpen.Returns(true);
        _actor.SendAsync(Arg.Any<IPrinterIntent>(), Arg.Any<CancellationToken>())
              .Returns(new CommandSendResult(CommandSendOutcome.Completed, new CommandOutcome(PrinterEventType.Finished, null)));
        _registry.Register(PrinterId, _actor, overPlaintext: false);

        context.PrinterLiveStates.Add(new PrinterLiveState
        {
            PrinterId = PrinterId,
            Status = PrinterStatus.Idle,
            LastSeenAt = DateTimeOffset.UtcNow,
        });

        context.PrinterTools.Add(new PrinterTool { PrinterId = PrinterId, ToolNumber = 1 });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

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
}
