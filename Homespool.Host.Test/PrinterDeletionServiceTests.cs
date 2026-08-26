using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

using NSubstitute;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect;
using Homespool.Host.Queue;
using Homespool.Host.Services;
using Homespool.Host.Telemetry;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrinterDeletionService"/> - the ordered teardown behind the delete button, and the
/// two gates in front of it.
/// </summary>
/// <remarks>
/// Real SQLite rather than the in-memory provider, like the other service tests here, and for a
/// reason this suite depends on more than most: <b>the in-memory provider does not enforce foreign
/// keys or run cascades</b>, so the assertions about what a deleted printer takes with it would pass
/// against a provider that had deleted nothing at all.
/// </remarks>
public sealed class PrinterDeletionServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-printerdelete-{Guid.NewGuid():N}.db");

    private readonly FakeLogger<PrinterConnectionRegistry> _registryLogger = new();

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

    /// <summary>
    /// Everything a printer owns, deleted in one go by the cascades rather than by hand here.
    /// </summary>
    [Fact]
    public async Task DeletingAPrinterTakesItsTelemetryEventsAndCamerasWithIt()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId, name: "Workshop");

        context.PrinterLiveStates.Add(new PrinterLiveState { PrinterId = printer.Id });
        context.TelemetrySamples.Add(new TelemetrySample { PrinterId = printer.Id, Timestamp = DateTimeOffset.UtcNow });
        context.PrinterEvents.Add(new PrinterEvent
        {
            PrinterId = printer.Id,
            Timestamp = DateTimeOffset.UtcNow,
            EventType = PrinterEventType.Info,
        });
        context.Cameras.Add(new Camera
        {
            Uuid = Guid.NewGuid(),
            TeamId = membership.TeamId,
            PrinterId = printer.Id,
            Source = "http://camera.invalid/snapshot",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        string? name = await NewService(context).DeletePrinterAsync(printer.Uuid, Caller.Unscoped(1), CancellationToken.None);

        // Assert
        name.Should().Be("Workshop");

        await using HomespoolDbContext verification = NewContext();

        verification.Printers.Should().BeEmpty();
        verification.PrinterLiveStates.Should().BeEmpty();
        verification.TelemetrySamples.Should().BeEmpty();
        verification.PrinterEvents.Should().BeEmpty();
        verification.Cameras.Should().BeEmpty();

        // The team is not a possession of the printer's and stays.
        verification.Teams.Should().ContainSingle();
    }

    /// <summary>
    /// The writer is told before the row goes, not after - which is the whole reason this service
    /// exists rather than a <c>Remove</c> at the call site.
    /// </summary>
    /// <remarks>
    /// A flush commits its batch in one transaction and keeps the buffers when it fails, so a row
    /// still buffered for a deleted printer stops telemetry persisting for <em>every</em> printer.
    /// Asserting the call is the cheap half; <c>TelemetryWriterTests</c> asserts that acting on it
    /// actually clears the buffers.
    /// </remarks>
    [Fact]
    public async Task DeletingAPrinterTellsTheTelemetryWriterToForgetIt()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        ITelemetryEviction telemetry = Substitute.For<ITelemetryEviction>();

        // Act
        await NewService(context, telemetry: telemetry)
            .DeletePrinterAsync(printer.Uuid, Caller.Unscoped(1), CancellationToken.None);

        // Assert
        await telemetry.Received(1).ForgetPrinterAsync(printer.Id, Arg.Any<CancellationToken>());
    }

    /// <summary>A live connection is shut down, so the printer stops writing to a row that is going.</summary>
    [Fact]
    public async Task DeletingAConnectedPrinterClosesItsConnection()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        await SetLiveStatusAsync(context, printer.Id, PrinterStatus.Idle);

        PrinterConnectionRegistry registry = new(_registryLogger);
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.IsOpen.Returns(true);
        registry.Register(printer.Id, actor);

        // Act
        await NewService(context, registry).DeletePrinterAsync(printer.Uuid, Caller.Unscoped(1), CancellationToken.None);

        // Assert
        actor.Received(1).Complete();
    }

    /// <summary>A connected printer that is mid-something refuses, and names the state.</summary>
    [Fact]
    public async Task AConnectedPrinterThatIsPrintingRefuses()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        await SetLiveStatusAsync(context, printer.Id, PrinterStatus.Printing);

        PrinterConnectionRegistry registry = new(_registryLogger);
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.IsOpen.Returns(true);
        registry.Register(printer.Id, actor);

        // Act
        Func<Task> deleting = () => NewService(context, registry)
            .DeletePrinterAsync(printer.Uuid, Caller.Unscoped(1), CancellationToken.None);

        // Assert
        (await deleting.Should().ThrowAsync<PrinterBusyException>()).Which.Status.Should().Be(PrinterStatus.Printing);

        await using HomespoolDbContext verification = NewContext();
        verification.Printers.Should().ContainSingle();
    }

    /// <summary>
    /// <b>The same stale <c>Printing</c>, but on a printer nobody can reach, deletes.</b>
    /// </summary>
    /// <remarks>
    /// This is the case the feature is mostly for - a printer that was unplugged, sold or died - and
    /// it is only a separate test because the naive guard gets it exactly backwards. Nothing ever
    /// writes <see cref="PrinterStatus.Offline"/> into <see cref="PrinterLiveState"/>, so a printer
    /// pulled from the wall mid-print reports <c>Printing</c> for ever. Guarding on that value alone
    /// would leave the machine in a skip as the one printer that could never be deleted.
    /// </remarks>
    [Fact]
    public async Task ADisconnectedPrinterDeletesEvenThoughItsLastKnownStateWasPrinting()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        await SetLiveStatusAsync(context, printer.Id, PrinterStatus.Printing);

        // Act - nothing registered, so nothing is connected.
        await NewService(context).DeletePrinterAsync(printer.Uuid, Caller.Unscoped(1), CancellationToken.None);

        // Assert
        await using HomespoolDbContext verification = NewContext();
        verification.Printers.Should().BeEmpty();
    }

    /// <summary>A printer that has never reported at all deletes - it has no live state to consult.</summary>
    [Fact]
    public async Task APrinterThatNeverConnectedDeletes()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        // Act
        await NewService(context).DeletePrinterAsync(printer.Uuid, Caller.Unscoped(1), CancellationToken.None);

        // Assert
        await using HomespoolDbContext verification = NewContext();
        verification.Printers.Should().BeEmpty();
    }

    /// <summary>
    /// A caller who can see the printer but not manage it is refused by name, having already been
    /// shown that it exists.
    /// </summary>
    [Fact]
    public async Task ACallerWithoutManagePrinterIsRefused()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canManage: false);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        // Act
        Func<Task> deleting = () => NewService(context)
            .DeletePrinterAsync(printer.Uuid, Caller.Unscoped(1), CancellationToken.None);

        // Assert
        await deleting.Should().ThrowAsync<TeamAccessDeniedException>();

        await using HomespoolDbContext verification = NewContext();
        verification.Printers.Should().ContainSingle();
    }

    /// <summary>
    /// A caller who cannot see the printer gets <c>null</c>, not a refusal - naming one would confirm
    /// the UUID belongs to something.
    /// </summary>
    [Fact]
    public async Task ACallerOnAnotherTeamIsToldNothing()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember owner = await AddTeamAsync(context, userId: 1, canManage: true);
        Printer printer = await AddPrinterAsync(context, owner.TeamId);

        await AddTeamAsync(context, userId: 2, canManage: true);

        // Act
        string? name = await NewService(context)
            .DeletePrinterAsync(printer.Uuid, Caller.Unscoped(2), CancellationToken.None);

        // Assert
        name.Should().BeNull();

        await using HomespoolDbContext verification = NewContext();
        verification.Printers.Should().ContainSingle();
    }

    /// <summary>
    /// <b>A scoped credential that never named <c>ManagePrinter</c> cannot delete</b>, whatever its
    /// owner's membership says - the case a personal access token sitting in a script is.
    /// </summary>
    [Fact]
    public async Task AScopedCredentialThatCannotManageIsRefused()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        TeamMember membership = await AddTeamAsync(context, userId: 1, canManage: true);
        Printer printer = await AddPrinterAsync(context, membership.TeamId);

        Caller scoped = Caller.Scoped(1, CapabilitySet.Parse(CapabilitySet.Format([Capability.ViewPrinter])));

        // Act
        Func<Task> deleting = () => NewService(context).DeletePrinterAsync(printer.Uuid, scoped, CancellationToken.None);

        // Assert
        await deleting.Should().ThrowAsync<CredentialScopeDeniedException>();

        await using HomespoolDbContext verification = NewContext();
        verification.Printers.Should().ContainSingle();
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

    private PrinterDeletionService NewService(HomespoolDbContext context,
                                              PrinterConnectionRegistry? registry = null,
                                              ITelemetryEviction? telemetry = null)
    {
        registry ??= new PrinterConnectionRegistry(_registryLogger);

        return new PrinterDeletionService(
            context,
            new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance),
            new QueueSnapshotReader(context, registry, TimeProvider.System),
            registry,
            telemetry ?? Substitute.For<ITelemetryEviction>(),
            NullLogger<PrinterDeletionService>.Instance);
    }

    private static async Task<TeamMember> AddTeamAsync(HomespoolDbContext context, long userId, bool canManage)
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
                    Capabilities = TestMemberships.Graded(canRead: true, canUse: true, canManage),
                    IsDefault = true,
                },
            },
        };

        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return team.Members.Single();
    }

    private static async Task<Printer> AddPrinterAsync(HomespoolDbContext context, int teamId, string? name = null)
    {
        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            Type = PrinterType.PrusaConnect,
            TeamId = teamId,
            Name = name,
            Status = PrinterStatus.Unknown,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return printer;
    }

    /// <summary>
    /// Writes the printer's <em>live</em> status, which is the only one that means anything -
    /// <c>Printer.Status</c> is written once as <c>Unknown</c> and never updated.
    /// </summary>
    private static async Task SetLiveStatusAsync(HomespoolDbContext context, int printerId, PrinterStatus status)
    {
        context.PrinterLiveStates.Add(new PrinterLiveState { PrinterId = printerId, Status = status });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
