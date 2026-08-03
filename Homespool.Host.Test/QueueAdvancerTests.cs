using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Homespool.Data;
using Homespool.Host.PrintFiles;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.PrusaConnect.Transfers;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="QueueAdvancer"/>'s own decisions - the ones that need a clock or a printer's answer, and
/// so cannot be reached through <see cref="QueueRules"/>.
/// </summary>
/// <remarks>
/// <para>
/// These were the untested half of the loop. Every case here is a rule that only fires when something
/// goes wrong - a print that never begins, a printer refusing for a reason that will not change - and
/// each one either holds a queue open or throws work away, so being wrong is expensive and silent.
/// </para>
/// <para>
/// Driven through <see cref="QueueAdvancer.AdvanceAsync"/> against real SQLite, with a settable clock
/// and a substituted actor. The advancer resolves what it needs per pass from a scope, so the
/// container here provides only what the path under test actually reaches.
/// </para>
/// </remarks>
public sealed class QueueAdvancerTests : IDisposable
{
    private const int PrinterId = 1;

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-advancer-{Guid.NewGuid():N}.db");
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UnixEpoch.AddYears(56));
    private readonly PrinterConnectionRegistry _registry = new(NullLogger<PrinterConnectionRegistry>.Instance);
    private readonly QueueSignal _signal = new();
    private readonly string _storeRoot = Path.Combine(Path.GetTempPath(), "hs-advancer-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _signal.Dispose();

        if (Directory.Exists(_storeRoot))
        {
            Directory.Delete(_storeRoot, recursive: true);
        }

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// A print accepted and never begun is closed rather than left open forever.
    /// </summary>
    /// <remarks>
    /// The bound the <c>Starting</c> phase needs. Ordinarily this window is seconds - 3.1 s measured on
    /// a Core One - but a heat-up that fails or a dialog nobody answers would otherwise leave the row
    /// open, and the partial unique index on <c>(PrinterId)</c> filtered to <c>EndedAt IS NULL</c>
    /// would then block every later print on that printer. <c>Unknown</c> rather than a guess: nothing
    /// here can say what happened.
    /// </remarks>
    [Fact]
    public async Task APrintThatNeverStartsIsClosedAsUnknown()
    {
        // Arrange - a row that has been Starting for longer than the bound allows
        await using HSDbContext context = await SeedAsync();

        context.PrintJobs.Add(new PrintJob
        {
            PrinterId = PrinterId,
            FileName = "stuck.bgcode",
            QueuedByUserId = 1,
            StartedAt = _clock.GetUtcNow(),
            Outcome = PrintOutcome.Starting,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        _clock.Advance(QueueAdvancer.StartingStaleAfter + TimeSpan.FromMinutes(1));

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        job.Outcome.Should().Be(PrintOutcome.Unknown);
        job.EndedAt.Should().NotBeNull("an open row would block this printer for good");
    }

    /// <summary>And it is left alone while it is still plausibly starting.</summary>
    [Fact]
    public async Task APrintStillWithinItsStartingWindowIsLeftOpen()
    {
        // Arrange
        await using HSDbContext context = await SeedAsync();

        context.PrintJobs.Add(new PrintJob
        {
            PrinterId = PrinterId,
            FileName = "heating.bgcode",
            QueuedByUserId = 1,
            StartedAt = _clock.GetUtcNow(),
            Outcome = PrintOutcome.Starting,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        _clock.Advance(TimeSpan.FromSeconds(30));

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        job.Outcome.Should().Be(PrintOutcome.Starting);
        job.EndedAt.Should().BeNull("a cold chamber legitimately takes minutes");
    }

    /// <summary>
    /// <c>Forbidden path</c> will not change by retrying, so the entry is dropped - and recorded, or a
    /// queued print would vanish with nowhere to find out why.
    /// </summary>
    [Fact]
    public async Task ATerminalRefusalDropsTheEntryAndRecordsWhy()
    {
        // Arrange - a file already on the drive, a ready printer, and a printer that refuses
        await using HSDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
        ConnectRefusing("Forbidden path");

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0,
            "retrying a forbidden path would hide a misconfiguration behind a queue that looks slow");

        PrintJob failure = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);
        failure.Outcome.Should().Be(PrintOutcome.Failed);
        failure.Reason.Should().Be("Forbidden path");
        failure.EndedAt.Should().NotBeNull("nothing printed, so it opens and closes together");
    }

    /// <summary>
    /// <c>File not found</c> is the drive correcting us: the belief that the file is there is cleared
    /// so it will be sent again, and the entry stays queued.
    /// </summary>
    [Fact]
    public async Task FileNotFoundClearsTheDriveBeliefRatherThanFailingTheEntry()
    {
        // Arrange
        await using HSDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
        ConnectRefusing("File not found");

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1,
            "the print is still wanted - the bytes simply are not where we believed");
        (await context.PrintFilesOnPrinters.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0,
            "clearing the row is what makes the loop send the file again");
        (await context.PrintJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0,
            "nothing failed - this is a retry, not an outcome");
    }

    /// <summary>
    /// <c>Can't print now</c> is the one transient reason: nothing is dropped and nothing is recorded,
    /// because the next pass simply asks again.
    /// </summary>
    [Fact]
    public async Task ATransientRefusalChangesNothing()
    {
        // Arrange
        await using HSDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
        ConnectRefusing("Can't print now");

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        (await context.PrintFilesOnPrinters.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        (await context.PrintJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    /// <summary>
    /// An unrecognised reason waits rather than being treated as terminal - a future firmware adding a
    /// string should not cost somebody their print.
    /// </summary>
    [Fact]
    public async Task AnUnknownRefusalIsTreatedAsTransient()
    {
        // Arrange
        await using HSDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
        ConnectRefusing("Something firmware has not said before");

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1,
            "throwing a print away for a string nobody has read yet would be the wrong default");
    }

    /// <summary>
    /// A transfer that has been "in flight" for longer than any real one could be is treated as gone,
    /// and the file is offered again.
    /// </summary>
    /// <remarks>
    /// <b>The case with no other bound.</b> A server restarted mid-transfer leaves
    /// <c>TransferStartedAt</c> set with nothing running and no terminal event ever coming - so
    /// without this, that printer's queue is wedged permanently and silently. Offering a second time
    /// is harmless: the printer either takes it or says its transfer slot is busy, which is the same
    /// waiting the loop was already doing.
    /// </remarks>
    [Fact]
    public async Task ATransferThatWentStaleIsOfferedAgain()
    {
        // Arrange - a transfer stamped long ago, and the bytes on disk for it
        await using HSDbContext context = await SeedAsync(status: PrinterStatus.Idle);
        await WriteFileOnDiskAsync("queued.bgcode");

        PrintFile file = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            TransferStartedAt = _clock.GetUtcNow(),
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        ConnectRefusing("Another transfer in progress");

        _clock.Advance(QueueAdvancer.TransferStaleAfter + TimeSpan.FromMinutes(1));

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert - it tried again, which the refusal above records by clearing the stamp
        context.ChangeTracker.Clear();

        PrintFileOnPrinter row = await context.PrintFilesOnPrinters
            .SingleAsync(TestContext.Current.CancellationToken);

        row.TransferStartedAt.Should().BeNull(
            "a stale stamp must not be mistaken for a transfer still running, or the queue wedges forever");
    }

    /// <summary>And a transfer that is merely slow is left alone.</summary>
    [Fact]
    public async Task ATransferStillWithinItsWindowIsNotDisturbed()
    {
        // Arrange
        await using HSDbContext context = await SeedAsync(status: PrinterStatus.Idle);
        await WriteFileOnDiskAsync("queued.bgcode");

        PrintFile file = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);
        DateTimeOffset started = _clock.GetUtcNow();

        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            TransferStartedAt = started,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        ConnectRefusing("Another transfer in progress");

        // A full-size model over TLS legitimately takes minutes.
        _clock.Advance(TimeSpan.FromMinutes(5));

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        PrintFileOnPrinter row = await context.PrintFilesOnPrinters
            .SingleAsync(TestContext.Current.CancellationToken);

        row.TransferStartedAt.Should().Be(started, "nothing should interrupt a transfer that is merely slow");
    }

    /// <summary>Puts real bytes where the store expects this user's file.</summary>
    private async Task WriteFileOnDiskAsync(string name)
    {
        string directory = Path.Combine(_storeRoot, "1-owner");
        Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(Path.Combine(directory, name), "G28 ; home\n",
            TestContext.Current.CancellationToken);
    }

    /// <summary>Registers a connected printer whose every command comes back refused.</summary>
    private void ConnectRefusing(string reason)
    {
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.IsOpen.Returns(true);
        actor.SendCommandAsync(Arg.Any<ISendableCommand>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new CommandSendResult(CommandSendOutcome.Completed,
                 new CommandOutcome(Events.Rejected, reason))));

        _registry.Register(PrinterId, actor);
    }

    private QueueAdvancer NewAdvancer()
    {
        ServiceCollection services = new();
        services.AddDbContext<HSDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
        services.AddScoped<TeamService>();
        services.AddSingleton(_registry);
        services.AddScoped<PrinterCommandService>();

        // The transfer path resolves these. Rooted in a temp directory: the staleness rule is about a
        // timestamp, and the file merely has to exist for the loop to get that far.
        services.Configure<PrintFileStorageOptions>(options => options.Directory = _storeRoot);
        services.AddSingleton<IHostEnvironmentAccessor>(new HostEnvironmentAccessor(_storeRoot));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<UserFileStore>();
        services.AddScoped<PrintFileCatalog>();
        services.AddSingleton<ITransferOffers>(
            new TransferOfferStore(TimeProvider.System, NullLogger<TransferOfferStore>.Instance));
        services.AddScoped<PrintFileSender>();
        services.AddLogging();

        return new QueueAdvancer(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            _registry,
            _signal,
            _clock,
            NullLogger<QueueAdvancer>.Instance);
    }

    /// <summary>A user, a team, a printer, a file, and one thing queued on it.</summary>
    private async Task<HSDbContext> SeedAsync(bool arrived = false, PrinterStatus status = PrinterStatus.Idle)
    {
        DbContextOptions<HSDbContext> options = new DbContextOptionsBuilder<HSDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        HSDbContext context = new(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        const string email = "owner@example.com";
        context.Users.Add(new HSUser(email)
        {
            Id = 1,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
        });

        Team team = new() { Name = "team" };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TeamMembers.Add(new TeamMember
        {
            TeamId = team.Id,
            UserId = 1,
            CanRead = true,
            CanUse = true,
        });

        context.Printers.Add(new Printer { Id = PrinterId, Uuid = Guid.NewGuid(), TeamId = team.Id });

        PrintFile file = new()
        {
            UserId = 1,
            Name = "queued.bgcode",
            Size = 1024,
            UploadedAt = _clock.GetUtcNow(),
        };

        context.PrintFiles.Add(file);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.QueuedPrints.Add(new QueuedPrint
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            Position = 0,
            QueuedByUserId = 1,
            QueuedAt = _clock.GetUtcNow(),
        });

        if (arrived)
        {
            context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
            {
                PrinterId = PrinterId,
                PrintFileId = file.Id,
                ArrivedAt = _clock.GetUtcNow(),
                PrinterPath = "/usb/QUEUED~1.BGC",
            });
        }

        context.PrinterLiveStates.Add(new PrinterLiveState
        {
            PrinterId = PrinterId,
            Status = status,
            LastSeenAt = _clock.GetUtcNow(),
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return context;
    }
}
