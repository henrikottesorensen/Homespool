using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.PrintFiles;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.PrusaConnect.Transfers;
using Homespool.Host.Queue;
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

    /// <summary>
    /// What <see cref="WriteFileOnDiskAsync"/> actually writes. The store reads the length off disk,
    /// so this - not the seeded <c>PrintFile.Size</c> - is what a drive's copy is compared against.
    /// </summary>
    private const long OnDiskLength = 11;

    /// <summary>
    /// A drive path with a terminal escape in it, spelt as JSON spells it - the rigs below build the
    /// printer's answer by interpolation, and the wire is where a printer would put one.
    /// </summary>
    private const string DirtyPathJson = "/usb/ALIEN\\u001B[2J.BGC";

    private const string CleanedPath = "/usb/ALIEN\uFFFD[2J.BGC";

    /// <summary>The seeded file's name with its owner's added - what a second file of its name is sent as.</summary>
    private const string OwnersName = "queued (owner@example.com).bgcode";

    /// <summary>
    /// The seeded file's digest, and its arrived copy's - the copy is of the file as it is. Not the
    /// digest of what <see cref="WriteFileOnDiskAsync"/> writes, which nothing here compares.
    /// </summary>
    private const string SeededDigest = "seeded-digest";

    /// <summary>The seeded file's digest after <see cref="OverwriteSeededFileAsync"/>.</summary>
    private const string OverwrittenDigest = "overwritten-digest";

    /// <summary>The handle the seeded entry is enqueued under - fixed, so assertions can name it.</summary>
    private static readonly Guid QueuedPrintUuid = new("11111111-2222-3333-4444-555555555555");

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
        await using HomespoolDbContext context = await SeedAsync();

        context.PrintJobs.Add(new PrintJob
        {
            PrinterId = PrinterId,
            FileName = "stuck.bgcode",
            QueuedByUserId = 1,
            StartedAt = _clock.GetUtcNow(),
            State = PrintState.Starting,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        _clock.Advance(QueueAdvancer.StartingStaleAfter + TimeSpan.FromMinutes(1));

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        job.State.Should().Be(PrintState.Unknown);
        job.EndedAt.Should().NotBeNull("an open row would block this printer for good");
    }

    /// <summary>
    /// At the bound, the row is closed on what the printer says rather than on a guess.
    /// </summary>
    /// <remarks>
    /// <b>The row closes either way</b>, so the only thing in question is whether print history says
    /// what happened. Firmware keeps the outcome of its last two jobs and answers for them by id -
    /// including a print aborted before it ever began, which is exactly this row. Asking costs one
    /// command at a moment the loop was about to guess.
    /// </remarks>
    [Fact]
    public async Task AtTheBoundThePrinterIsAskedHowThePrintEndedRatherThanGuessing()
    {
        // Arrange - stranded with a job id the printer still remembers
        await using HomespoolDbContext context = await SeedAsync();

        context.PrintJobs.Add(new PrintJob
        {
            PrinterId = PrinterId,
            FileName = "stuck.bgcode",
            QueuedByUserId = 1,
            QueuedByScope = CapabilitySet.Format(CapabilitySet.Everything),
            StartedAt = _clock.GetUtcNow(),
            State = PrintState.Starting,
            FirmwareJobId = 752,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        ConnectRememberingJobOutcome("FIN_STOPPED");
        await ReportAsync(context, PrinterStatus.Attention, jobId: 752);
        _clock.Advance(QueueAdvancer.StartingStaleAfter + TimeSpan.FromMinutes(1));

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        job.EndedAt.Should().NotBeNull();
        job.State.Should().Be(PrintState.Stopped, "the printer remembered, so Unknown would be a guess it did not have to make");
    }

    /// <summary>
    /// A row with no recorded scope is closed as <c>Unknown</c> without asking, rather than asked
    /// about on invented authority.
    /// </summary>
    /// <remarks>
    /// Rows opened before <see cref="PrintJob.QueuedByScope"/> existed have no credential to borrow,
    /// and acting as the user without one would run the command with more authority than anybody
    /// granted. Under-asking costs a guess in the history; the alternative costs more.
    /// </remarks>
    [Fact]
    public async Task ARowWithNoRecordedScopeIsClosedWithoutAsking()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();

        context.PrintJobs.Add(new PrintJob
        {
            PrinterId = PrinterId,
            FileName = "legacy.bgcode",
            QueuedByUserId = 1,
            QueuedByScope = null,
            StartedAt = _clock.GetUtcNow(),
            State = PrintState.Starting,
            FirmwareJobId = 752,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        IPrinterConnectionActor actor = ConnectRememberingJobOutcome("FIN_STOPPED");
        await ReportAsync(context, PrinterStatus.Attention, jobId: 752);
        _clock.Advance(QueueAdvancer.StartingStaleAfter + TimeSpan.FromMinutes(1));

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert - the printer would have answered; it was never asked, which is the point. Asserting
        // only the Unknown outcome would pass with the guard removed, because an empty scope is
        // refused at the send and lands on the same answer by a different road.
        await actor.DidNotReceive().SendCommandAsync(Arg.Any<SendJobInfo>(), Arg.Any<CancellationToken>());

        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        job.EndedAt.Should().NotBeNull("the row still has to close, or the printer is wedged");
        job.State.Should().Be(PrintState.Unknown, "there was no authority to ask with");
    }

    /// <summary>And it is left alone while it is still plausibly starting.</summary>
    [Fact]
    public async Task APrintStillWithinItsStartingWindowIsLeftOpen()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();

        context.PrintJobs.Add(new PrintJob
        {
            PrinterId = PrinterId,
            FileName = "heating.bgcode",
            QueuedByUserId = 1,
            StartedAt = _clock.GetUtcNow(),
            State = PrintState.Starting,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        _clock.Advance(TimeSpan.FromSeconds(30));

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        job.State.Should().Be(PrintState.Starting);
        job.EndedAt.Should().BeNull("a cold chamber legitimately takes minutes");
    }

    /// <summary>
    /// A print taken into the panel's preview and then ended there is closed on the withdrawn job id,
    /// not on the fifteen-minute bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The case the bound used to swallow whole.</b> Firmware reports <c>PRINTING</c> for about a
    /// second before a preview dialog takes over as <c>ATTENTION</c>, which a five-second poll misses
    /// almost every time - so the row never promotes, and every later status fell through to the
    /// bound. Measured four times on hardware at 901 s, 904 s, 15m01s and 15m02s, with the printer
    /// idle and available within a second of the person acting.
    /// </para>
    /// <para>
    /// <b>Two passes, because that is the shape of the evidence.</b> The first sees the job id while
    /// the dialog is up and records it; the second sees it withdrawn. Neither alone is enough, which
    /// is the whole reason the id is stored rather than inspected in the moment.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APrintEndedAtThePanelIsClosedOnTheWithdrawnJobIdRatherThanTheBound()
    {
        // Arrange - commanded, acknowledged, and taken into a preview dialog carrying job 752
        await using HomespoolDbContext context = await SeedAsync();

        context.PrintJobs.Add(new PrintJob
        {
            PrinterId = PrinterId,
            FileName = "mismatched.bgcode",
            QueuedByUserId = 1,
            StartedAt = _clock.GetUtcNow(),
            State = PrintState.Starting,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using QueueAdvancer advancer = NewAdvancer();

        await ReportAsync(context, PrinterStatus.Attention, jobId: 752);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        context.ChangeTracker.Clear();
        PrintJob held = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        held.EndedAt.Should().BeNull("a dialog the person can still answer keeps its row");
        held.FirmwareJobId.Should().Be(752, "the offered job id is the evidence a later pass needs");

        // Act - the dialog is answered at the panel: the printer goes idle and reports no job
        await ReportAsync(context, PrinterStatus.Idle, jobId: null);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        job.EndedAt.Should().NotBeNull("the printer took the job and now reports none - it is over");
        job.State.Should().Be(PrintState.Unknown);

        (_clock.GetUtcNow() - job.StartedAt).Should()
            .BeLessThan(QueueAdvancer.StartingStaleAfter,
                        "closing must come from the evidence, not from waiting the bound out");
    }

    /// <summary>
    /// A dialog still standing keeps its row, because the person at the machine can still answer it.
    /// </summary>
    /// <remarks>
    /// The counterweight to the test above, and the reason the bound is not simply shortened: the
    /// queue entry is consumed at the ack, so a row closed while <c>Print</c> is still pressable would
    /// let that print run with no row and no entry left to adopt it against.
    /// </remarks>
    [Fact]
    public async Task ADialogStillCarryingOurJobIdKeepsTheRowOpen()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();

        context.PrintJobs.Add(new PrintJob
        {
            PrinterId = PrinterId,
            FileName = "waiting.bgcode",
            QueuedByUserId = 1,
            StartedAt = _clock.GetUtcNow(),
            State = PrintState.Starting,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using QueueAdvancer advancer = NewAdvancer();

        // Act - five minutes of an unanswered dialog, well past any plausible start window
        await ReportAsync(context, PrinterStatus.Attention, jobId: 800);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        job.State.Should().Be(PrintState.Starting);
        job.EndedAt.Should().BeNull("nobody has answered it yet, and Print is still pressable");
    }

    /// <summary>
    /// An idle printer that has never offered a job id is still starting, not finished.
    /// </summary>
    /// <remarks>
    /// <b>This is what the <c>FirmwareJobId</c> guard buys.</b> Firmware maps <c>PrintInit</c> and
    /// <c>PrintPreviewInit</c> to <c>Idle</c>/<c>Ready</c> while it opens the file, carrying no job id
    /// - measured at 1.0-7 s. Closing on "idle and no job" without the guard would kill every print in
    /// its first seconds.
    /// </remarks>
    [Fact]
    public async Task AnIdlePrinterThatHasNeverReportedAJobIsStillStarting()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();

        context.PrintJobs.Add(new PrintJob
        {
            PrinterId = PrinterId,
            FileName = "opening.bgcode",
            QueuedByUserId = 1,
            StartedAt = _clock.GetUtcNow(),
            State = PrintState.Starting,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act - PrintInit reports Idle with no job id
        await ReportAsync(context, PrinterStatus.Idle, jobId: null);
        _clock.Advance(TimeSpan.FromSeconds(4));

        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        job.State.Should().Be(PrintState.Starting);
        job.EndedAt.Should().BeNull("a printer opening the file reports no job id yet");
    }

    /// <summary>A printer that says it stopped is believed, without waiting the bound out.</summary>
    [Fact]
    public async Task APrintThatNeverBeganAndIsReportedStoppedIsClosedAsStopped()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();

        context.PrintJobs.Add(new PrintJob
        {
            PrinterId = PrinterId,
            FileName = "aborted.bgcode",
            QueuedByUserId = 1,
            StartedAt = _clock.GetUtcNow(),
            State = PrintState.Starting,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await ReportAsync(context, PrinterStatus.Stopped, jobId: null);
        _clock.Advance(TimeSpan.FromSeconds(5));

        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        job.State.Should().Be(PrintState.Stopped, "the printer said so - there is nothing to wait for");
        job.EndedAt.Should().NotBeNull();
    }

    /// <summary>
    /// <c>Forbidden path</c> will not change by retrying, so the entry is dropped - and recorded, or a
    /// queued print would vanish with nowhere to find out why.
    /// </summary>
    [Fact]
    public async Task ATerminalRefusalDropsTheEntryAndRecordsWhy()
    {
        // Arrange - a file already on the drive, a ready printer, and a printer that refuses
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
        ConnectRefusing("Forbidden path");

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0,
            "retrying a forbidden path would hide a misconfiguration behind a queue that looks slow");

        PrintJob failure = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);
        failure.State.Should().Be(PrintState.Failed);
        failure.Reason.Should().Be("Forbidden path");
        failure.EndedAt.Should().NotBeNull("nothing printed, so it opens and closes together");
        failure.PrintUuid.Should().Be(QueuedPrintUuid,
                                       "the refusal is findable by the handle the enqueue returned - the row used to exist and be unreachable");
    }

    /// <summary>
    /// A printer whose queue is empty but whose print is still open gets a pass - through
    /// <see cref="QueueAdvancer.AdvanceAllAsync"/>, which is the point.
    /// </summary>
    /// <remarks>
    /// <b>The regression test for the 2026-08-04 blind spot, and it must go through
    /// <c>AdvanceAllAsync</c>.</b> Every other test hand-picks its printer via
    /// <c>AdvanceAsync(printerId)</c>, which is why none of them could see the defect: the selection
    /// query had zero coverage, and the shipped app showed "Printing now" forever once the last queue
    /// entry was consumed. Against the old predicate this fails exactly here - <c>AdvanceAllAsync</c>
    /// finds nothing to visit.
    /// </remarks>
    [Fact]
    public async Task APrinterWithAnOpenPrintAndAnEmptyQueueStillGetsAPass()
    {
        // Arrange - the moment after START_PRINT consumed the last entry: no queue, one open row,
        // and the printer has since stopped.
        await using HomespoolDbContext context = await SeedAsync(status: PrinterStatus.Stopped);

        context.QueuedPrints.RemoveRange(context.QueuedPrints);
        context.PrintJobs.Add(new PrintJob
        {
            PrinterId = PrinterId,
            FileName = "last.bgcode",
            QueuedByUserId = 1,
            StartedAt = _clock.GetUtcNow(),
            State = PrintState.Printing,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act - the whole loop, not a hand-picked printer
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAllAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        job.EndedAt.Should().NotBeNull("the pass must reach a printer no queue entry names any more");
        job.State.Should().Be(PrintState.Stopped);
    }

    /// <summary>
    /// <c>File not found</c> is the drive correcting us: the belief that the file is there is cleared
    /// so it will be sent again, and the entry stays queued.
    /// </summary>
    [Fact]
    public async Task FileNotFoundClearsTheDriveBeliefRatherThanFailingTheEntry()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
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
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
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
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
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
        await using HomespoolDbContext context = await SeedAsync(status: PrinterStatus.Idle);
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
        await using HomespoolDbContext context = await SeedAsync(status: PrinterStatus.Idle);
        await WriteFileOnDiskAsync("queued.bgcode");

        PrintFile file = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);
        DateTimeOffset started = _clock.GetUtcNow();

        // Reported by the printer a few seconds in, which is what makes the stamp a transfer running
        // rather than one merely started.
        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            TransferStartedAt = started,
            PrinterPath = "/usb/QUEUED~1.BGC",
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

    /// <summary>
    /// <b>The loop acts within the authority the work was accepted under, not merely as its owner.</b>
    /// An entry whose recorded scope cannot print is left alone, though the person who queued it can
    /// print perfectly well.
    /// </summary>
    /// <remarks>
    /// Without the stored scope the membership half would be re-checked at send time and the
    /// credential half would not, so a narrowly scoped token could queue work that then ran with its
    /// owner's full rights - privilege escalation across a time boundary. Latent while the loop only
    /// does what <c>Print</c> covers; the point of storing the scope is that it stops being latent the
    /// day the loop gains a step.
    /// </remarks>
    [Fact]
    public async Task AnEntryQueuedUnderAScopeThatCannotPrintIsNotActedOn()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);

        QueuedPrint head = await context.QueuedPrints.SingleAsync(TestContext.Current.CancellationToken);

        // A printer that would happily accept, so a refusal can only come from the scope.
        ConnectAccepting();

        // The owner may print - SeedAsync grants it. The credential that queued this may not.
        head.QueuedByScope = CapabilitySet.Format([Capability.ViewPrinter, Capability.ViewQueue]);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        context.PrintJobs.Should().BeEmpty("nothing may be started under a scope that cannot print");

        (await context.QueuedPrints.SingleAsync(TestContext.Current.CancellationToken))
            .Should().NotBeNull("and the entry stays where it is rather than being consumed");
    }

    /// <summary>
    /// The ordinary case beside it: an entry queued under a scope that <i>can</i> print is started, so
    /// the test above is measuring the scope rather than a loop that does nothing.
    /// </summary>
    [Fact]
    public async Task AnEntryQueuedUnderAScopeThatCanPrintIsStarted()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);

        ConnectAccepting();

        QueuedPrint head = await context.QueuedPrints.SingleAsync(TestContext.Current.CancellationToken);
        head.QueuedByScope = CapabilitySet.Format([Capability.Print]);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        context.PrintJobs.Should().NotBeEmpty("Print is what queueing and starting both need");
    }

    /// <summary>
    /// <b>A closed account's queued print does not run</b>, and reopening the account resumes it.
    /// </summary>
    /// <remarks>
    /// The loop acts as whoever queued the work and never signs in, so the sign-in gate that refuses
    /// the person does nothing here: only the membership check at send time can. The entry is kept
    /// rather than consumed, as it is for a member who left the team - closing an account is not a
    /// decision about their prints.
    /// </remarks>
    [Fact]
    public async Task AClosedAccountsQueuedPrintIsNotStartedUntilItIsReopened()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);

        // A printer that would happily accept, so a refusal can only come from the account.
        ConnectAccepting();

        HSUser owner = await context.Users.SingleAsync(TestContext.Current.CancellationToken);
        owner.DeactivatedAt = _clock.GetUtcNow();
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using QueueAdvancer advancer = NewAdvancer();

        // Act
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        context.PrintJobs.Should().BeEmpty("nothing may be started on a closed account's authority");
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(1, "and the entry stays where it is rather than being consumed");

        // Act - reopened
        owner = await context.Users.SingleAsync(TestContext.Current.CancellationToken);
        owner.DeactivatedAt = null;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        context.PrintJobs.Should().NotBeEmpty("a reopened account's print carries on where it stopped");
    }

    /// <summary>
    /// The snapshot says why the queue stopped, so the page and the rules tell the same story as the
    /// gate - rather than answering "transfer" or "print" for a send that will be refused.
    /// </summary>
    [Fact]
    public async Task TheSnapshotSaysWhenTheHeadsQueuerMayNoLongerPrint()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
        await using TelemetryDbContext telemetry = TestTelemetryContext.For(context);
        ConnectAccepting();

        QueueSnapshot open = await NewSnapshotReader(context, telemetry).ReadAsync(PrinterId, TestContext.Current.CancellationToken);

        HSUser owner = await context.Users.SingleAsync(TestContext.Current.CancellationToken);
        owner.DeactivatedAt = _clock.GetUtcNow();
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        QueueSnapshot closed = await NewSnapshotReader(context, telemetry).ReadAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        open.HeadAuthorityLapsed.Should().BeFalse();
        QueueRules.Decide(open).Kind.Should().Be(QueueActionKind.Print);

        closed.HeadAuthorityLapsed.Should().BeTrue();
        QueueRules.Decide(closed).Reason.Should().Be(QueueWaitReason.QueuerLostAccess);
    }

    /// <summary>
    /// The same lapse from the other side: the account is open and its recorded scope never named
    /// <see cref="Capability.Print"/>.
    /// </summary>
    [Fact]
    public async Task TheSnapshotReadsTheRecordedScopeAsWellAsTheMembership()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
        await using TelemetryDbContext telemetry = TestTelemetryContext.For(context);

        QueuedPrint head = await context.QueuedPrints.SingleAsync(TestContext.Current.CancellationToken);
        head.QueuedByScope = CapabilitySet.Format([Capability.ViewPrinter, Capability.ViewQueue]);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        QueueSnapshot snapshot = await NewSnapshotReader(context, telemetry).ReadAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        snapshot.HeadAuthorityLapsed.Should().BeTrue("the loop acts on the credential that queued the work, not the owner's full rights");
    }

    /// <summary>A reader over this fixture's database, fresh so no earlier answer is memoised.</summary>
    private QueueSnapshotReader NewSnapshotReader(HomespoolDbContext context, TelemetryDbContext telemetry)
    {
        return new QueueSnapshotReader(context, telemetry, _registry, _clock,
                                       new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance),
                                       Substitute.For<ITransferOffers>());
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
                                                            new CommandOutcome(PrinterEventType.Rejected, reason))));
        actor.SendAsync(Arg.Any<IPrinterIntent>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new CommandSendResult(CommandSendOutcome.Completed,
                                                            new CommandOutcome(PrinterEventType.Rejected, reason))));

        _registry.Register(PrinterId, actor, overPlaintext: false);
    }

    /// <summary>A printer that accepts whatever it is sent, so a refusal can only be ours.</summary>
    private IPrinterConnectionActor ConnectAccepting()
    {
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.IsOpen.Returns(true);
        actor.SendCommandAsync(Arg.Any<ISendableCommand>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new CommandSendResult(CommandSendOutcome.Completed,
                                                            new CommandOutcome(PrinterEventType.Finished, null))));
        actor.SendAsync(Arg.Any<IPrinterIntent>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new CommandSendResult(CommandSendOutcome.Completed,
                                                            new CommandOutcome(PrinterEventType.Finished, null))));

        _registry.Register(PrinterId, actor, overPlaintext: false);

        return actor;
    }

    /// <summary>
    /// A printer that refuses the transfer with <c>FILE_EXISTS</c> and then answers
    /// <c>SEND_FILE_INFO</c> about whatever is already sitting there.
    /// </summary>
    /// <param name="existingSize">What the drive says the existing file's size is, or null to answer without one.</param>
    /// <param name="existingPath">The 8.3 alias the printer reports, which is what a print must use.</param>
    /// <param name="readOnly">Whether the drive reports the file in use - printing, or a transfer still arriving.</param>
    private IPrinterConnectionActor ConnectRefusingTransferAsExisting(long? existingSize,
                                                                      string existingPath = "/usb/SHAPE-~1.BGC",
                                                                      bool readOnly = false)
    {
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.IsOpen.Returns(true);

        // The print start travels as an intent; the transfer offer and the file-info query stay
        // wire-typed, so this printer answers on both faces of the actor.
        actor.SendAsync(Arg.Any<IPrinterIntent>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new CommandSendResult(CommandSendOutcome.Completed,
                                                            new CommandOutcome(PrinterEventType.Finished, null))));
        actor.SendCommandAsync(Arg.Any<ISendableCommand>(), Arg.Any<CancellationToken>())
             .Returns(call =>
             {
                 if (call.Arg<ISendableCommand>() is SendFileInfo)
                 {
                     string readOnlyField = readOnly ? ",\"read_only\":true" : string.Empty;
                     string json = existingSize is { } size ?
                         $"{{\"path\":\"{existingPath}\",\"size\":{size}{readOnlyField}}}" :
                         $"{{\"path\":\"{existingPath}\"{readOnlyField}}}";

                     return Task.FromResult(new CommandSendResult(CommandSendOutcome.Completed,
                                                                  new CommandOutcome(PrinterEventType.FileInfo, null),
                                                                  JsonSerializer.Deserialize<JsonElement>(json)));
                 }

                 return Task.FromResult(new CommandSendResult(CommandSendOutcome.Completed,
                                                              new CommandOutcome(PrinterEventType.Rejected, "File already exists")
                                                                  { MachineReason = "FILE_EXISTS" }));
             });

        _registry.Register(PrinterId, actor, overPlaintext: false);

        return actor;
    }

    /// <summary>Every drive path the printer was asked to fetch a file to, in order.</summary>
    private static string[] OfferedPaths(IPrinterConnectionActor actor)
    {
        return [.. actor.ReceivedCalls()
                        .Select(call => call.GetArguments().FirstOrDefault())
                        .Select(argument => argument switch
                        {
                            StartConnectDownload inline => inline.Path,
                            StartEncryptedDownload encrypted => encrypted.Path,
                            _ => null,
                        })
                        .OfType<string>()];
    }

    /// <summary>
    /// A copy of these very bytes that Homespool sent and never heard arrive - a direct send - is
    /// adopted under the name the printer uses, rather than refused as somebody else's.
    /// </summary>
    [Fact]
    public async Task OurOwnCopyAlreadyOnTheDriveIsAdopted()
    {
        // Arrange - the printer took a transfer of these bytes; the end of it was never seen.
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        await AddTakenCopyAsync(context);
        ConnectRefusingTransferAsExisting(existingSize: OnDiskLength);

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.ArrivedAt.Should().NotBeNull("these bytes are on the drive, and the digest says so");
        row.PrinterPath.Should().Be("/usb/SHAPE-~1.BGC",
                                    "the alias the printer answered with is what START_PRINT has to use, and it is unguessable from here");
        row.HoldReason.Should().BeNull("nothing is in the way");
    }

    /// <summary>
    /// A file of the same name and size that nobody recorded sending is not adopted - it could be any
    /// version of anything - and ours goes on under the next name.
    /// </summary>
    /// <remarks>
    /// <b>The rule that closes the overwrite's second road.</b> A re-slice that changes one
    /// temperature keeps its length, so a size match adopted the older version of a file as the newer
    /// one. <c>FILE_INFO</c> carries no digest; only what Homespool recorded sending can vouch for bytes.
    /// </remarks>
    [Fact]
    public async Task AFileNobodyRecordedIsNotAdoptedEvenAtTheSameSize()
    {
        // Arrange - nothing recorded; the drive holds something of our name at our size.
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        ConnectRefusingTransferAsExisting(existingSize: OnDiskLength);

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.ArrivedAt.Should().BeNull("a matching size is not matching content");
        row.PrinterPath.Should().BeNull();
        row.DriveName.Should().Be(OwnersName, "ours goes on under the next name, as for any file that is not ours");
    }

    /// <summary>
    /// A same-size file the drive reports read-only is not adopted: that is a file in use, and a partial
    /// still arriving is one - firmware preallocates it to its full size.
    /// </summary>
    [Fact]
    public async Task AFileOnTheDriveThatIsStillInUseIsNotAdopted()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        ConnectRefusingTransferAsExisting(existingSize: OnDiskLength, readOnly: true);

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.ArrivedAt.Should().BeNull("a partial at its preallocated size is not the file");
        row.PrinterPath.Should().BeNull();
        row.DriveName.Should().Be(OwnersName, "ours goes on under the next name, as for any file that is not ours");
    }

    /// <summary>
    /// Our own copy, read-only - still arriving, or being printed - is waited for under its name, not
    /// abandoned for a second copy under another.
    /// </summary>
    [Fact]
    public async Task OurOwnCopyStillInUseIsWaitedForUnderItsName()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        await AddTakenCopyAsync(context);
        ConnectRefusingTransferAsExisting(existingSize: OnDiskLength, readOnly: true);

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.DriveName.Should().Be("queued.bgcode", "the bytes under it are ours and this version");
        row.ArrivedAt.Should().BeNull("in use is not arrived");
        row.Digest.Should().Be(SeededDigest);
        row.HoldReason.Should().BeNull("it ends by itself");
    }

    /// <summary>
    /// A file of our name already on the drive at another size is somebody else's: ours goes on under
    /// its owner's name, rather than into theirs or into a hold until somebody clears the drive.
    /// </summary>
    [Fact]
    public async Task AFileAlreadyOnTheDriveAtADifferentSizeIsSentUnderTheOwnersName()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        IPrinterConnectionActor actor = ConnectRefusingTransferAsExisting(existingSize: OnDiskLength + 4096);
        using QueueAdvancer advancer = NewAdvancer();

        // Act - the pass that is refused, and the one after it
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        context.ChangeTracker.Clear();
        PrintFileOnPrinter refused = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        refused.DriveName.Should().Be(OwnersName, "the next name for the file is its own with its owner's added");
        refused.ArrivedAt.Should().BeNull("a matching name is not matching content");
        refused.HoldReason.Should().BeNull("a stranger's file is no reason to stop the queue");
        OfferedPaths(actor).Should().Equal(["/usb/queued.bgcode", "/usb/" + OwnersName],
                                           "the second attempt goes to the new name");

        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1,
            "somebody still wants this printed");
    }

    /// <summary>
    /// When every name the file could take is already on the drive, the queue holds as it always did,
    /// with both sizes stated.
    /// </summary>
    [Fact]
    public async Task WhenEveryNameIsTakenOnTheDriveTheQueueHolds()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        ConnectRefusingTransferAsExisting(existingSize: OnDiskLength + 4096);
        using QueueAdvancer advancer = NewAdvancer();

        // Act - one refused pass per name: its own, then every one with its owner's added
        for (int pass = 0; pass <= DriveNames.MaxNumbered; pass++)
        {
            await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);
        }

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.HoldReason.Should().Be(PrintHoldReason.FileExistsDifferentSize,
                                   "the reason has to reach a person, or the queue stalls silently");
        row.HoldPrinterFileBytes.Should().Be(OnDiskLength + 4096);
        row.BlockedAt.Should().NotBeNull("the hold is re-checked on a clock, not every tick");
    }

    /// <summary>
    /// Another member's file of the same name on this printer: ours is sent under its owner's name from
    /// the start, so the two never share a path on the drive and neither can be printed for the other.
    /// </summary>
    /// <param name="theirsArrived">
    /// Whether the other file is on the drive by the queue's own record, or only reserved there by a
    /// direct send, which records the name and nothing else.
    /// </param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnotherUsersFileOfTheSameNameMakesTheTransferUseTheOwnersName(bool theirsArrived)
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        await AddOtherUsersFileOnThePrinterAsync(context, "queued.bgcode", theirsArrived);
        IPrinterConnectionActor actor = ConnectAccepting();

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        OfferedPaths(actor).Should().Equal(["/usb/" + OwnersName]);

        context.ChangeTracker.Clear();
        (await context.PrintFilesOnPrinters.SingleAsync(row => row.PrintFileId == 1, TestContext.Current.CancellationToken))
            .DriveName.Should().Be(OwnersName);
    }

    /// <summary>A file sharing its printer with nobody is sent under its own name, and that is recorded.</summary>
    [Fact]
    public async Task AFileWithNoOtherOfItsNameIsSentUnderItsOwnName()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        IPrinterConnectionActor actor = ConnectAccepting();

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        OfferedPaths(actor).Should().Equal(["/usb/queued.bgcode"]);

        context.ChangeTracker.Clear();
        (await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken))
            .DriveName.Should().Be("queued.bgcode", "the printer's reports about it will carry this name");
    }

    /// <summary>
    /// A file overwritten since its copy arrived is sent again, the older copy deleted first, rather
    /// than printed from that copy under the newer file's name.
    /// </summary>
    /// <remarks>
    /// <b>The defect</b>: the copy stayed marked arrived through the overwrite, so the queue skipped
    /// the transfer, printed the old bytes and recorded the new digest in history - every time, until
    /// somebody deleted the copy at the printer.
    /// </remarks>
    [Fact]
    public async Task AnOverwrittenFileIsSentAgainRatherThanPrintedFromTheOlderCopy()
    {
        // Arrange - the copy arrived, and then the file was overwritten with other bytes
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        await OverwriteSeededFileAsync(context);
        IPrinterConnectionActor actor = ConnectAnswering(_ => Answered(PrinterEventType.Finished));

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        object[] sent = CommandsSent(actor);

        sent.OfType<Homespool.Host.Printing.StartPrint>().Should().BeEmpty("the copy on the drive is the older version");
        sent.OfType<DeleteFile>().Select(command => command.Path).Should().Equal(["/usb/queued.bgcode"]);
        Array.FindIndex(sent, command => command is DeleteFile).Should()
             .BeLessThan(Array.FindIndex(sent, command => command is StartConnectDownload or StartEncryptedDownload),
                         "the printer refuses a transfer onto a name it already holds");
        OfferedPaths(actor).Should().Equal(["/usb/queued.bgcode"], "the newer version goes where the older one was");

        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.Digest.Should().Be(OverwrittenDigest, "the printer took the newer bytes");
        row.ArrivedAt.Should().BeNull("and they have not arrived yet");
        (await context.PrintJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0,
            "nothing printed, so history claims nothing");
    }

    /// <summary>
    /// A file overwritten while its older version is still arriving is neither printed from the partial
    /// nor offered again until that transfer ends - and is then replaced.
    /// </summary>
    /// <remarks>
    /// The offer opened the file before the overwrite, so the older bytes are what arrives. Printing on
    /// the printer's first report, which the queue otherwise does, would print them.
    /// </remarks>
    [Fact]
    public async Task AFileOverwrittenWhileItsTransferRunsIsReplacedOnceThatTransferEnds()
    {
        // Arrange - the older version arriving and named by the printer; then the overwrite
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        PrintFile file = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            TransferStartedAt = _clock.GetUtcNow(),
            PrinterPath = "/usb/QUEUED~1.BGC",
            DriveName = file.Name,
            Digest = SeededDigest,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await OverwriteSeededFileAsync(context);
        IPrinterConnectionActor actor = ConnectAnswering(_ => Answered(PrinterEventType.Finished));
        using QueueAdvancer advancer = NewAdvancer();

        // Act - a pass while it runs
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        CommandsSent(actor).Should().BeEmpty("the partial is the older version, and the one transfer slot is taken");

        // Act - the older version finishes arriving, and the next pass
        await AddTransferEndAsync(PrinterEventType.TransferFinished, "/usb/queued.bgcode");
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        object[] sent = CommandsSent(actor);

        sent.OfType<Homespool.Host.Printing.StartPrint>().Should().BeEmpty("what arrived is the older version");
        sent.OfType<DeleteFile>().Should().ContainSingle();
        OfferedPaths(actor).Should().Equal(["/usb/queued.bgcode"]);
    }

    /// <summary>
    /// An older copy the printer is using is asked about again on the next pass, and never counts
    /// against the file or holds the queue.
    /// </summary>
    /// <remarks>
    /// Both end by themselves - a print of the copy finishes, a transfer of it completes - as a busy
    /// transfer slot frees up, and the queue waits on them the same way.
    /// </remarks>
    [Theory]
    [InlineData("File is busy")]
    [InlineData("File is being transferred")]
    [InlineData("This file is currently printed")]
    public async Task AnOlderCopyInUseIsWaitedForWithoutCountingAgainstTheFile(string reason)
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        await OverwriteSeededFileAsync(context);
        IPrinterConnectionActor actor = ConnectAnswering(command => command is DeleteFile ?
                                                             Answered(PrinterEventType.Rejected, reason) :
                                                             Answered(PrinterEventType.Finished));
        using QueueAdvancer advancer = NewAdvancer();
        int passes = TransferRetryRules.HoldAfter * 2;

        // Act
        for (int pass = 0; pass < passes; pass++)
        {
            await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);
            _clock.Advance(QueueAdvancer.PollInterval);
        }

        // Assert
        CommandsSent(actor).OfType<DeleteFile>().Should().HaveCount(passes, "asked again every pass, as a busy slot is");
        OfferedPaths(actor).Should().BeEmpty("the name is still taken");

        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.TransferRefusalCount.Should().BeNull("a copy in use says nothing about this file");
        row.HoldReason.Should().BeNull();
        row.Digest.Should().Be(SeededDigest, "the older copy is still there, and still recorded as older");
        (await context.PrintJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    /// <summary>
    /// An older copy the printer will not delete for any other reason is counted like a refused
    /// transfer - the file cannot be sent while it is there - and the next attempt waits.
    /// </summary>
    [Fact]
    public async Task AnOlderCopyThePrinterWillNotDeleteIsCountedLikeARefusedTransfer()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        await OverwriteSeededFileAsync(context);
        IPrinterConnectionActor actor = ConnectAnswering(command => command is DeleteFile ?
                                                             Answered(PrinterEventType.Rejected, "Error deleting file") :
                                                             Answered(PrinterEventType.Finished));
        using QueueAdvancer advancer = NewAdvancer();

        // Act - refused, then a pass before the wait has run out
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        CommandsSent(actor).OfType<DeleteFile>().Should().ContainSingle("the second pass waits, as after any refusal");
        OfferedPaths(actor).Should().BeEmpty();

        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.TransferRefusalCount.Should().Be(1);
        row.TransferRefusalReason.Should().Be("Error deleting file", "the printer's words are the useful part");
    }

    /// <summary>An older copy already gone from the drive is no obstacle: the newer one is sent at once.</summary>
    [Fact]
    public async Task AnOlderCopyAlreadyGoneFromTheDriveIsNotWaitedFor()
    {
        // Arrange - deleted at the panel since it arrived
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        await OverwriteSeededFileAsync(context);
        IPrinterConnectionActor actor = ConnectAnswering(command => command is DeleteFile ?
                                                             Answered(PrinterEventType.Rejected, "File not found") :
                                                             Answered(PrinterEventType.Finished));

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        OfferedPaths(actor).Should().Equal(["/usb/queued.bgcode"]);

        context.ChangeTracker.Clear();
        (await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken))
            .TransferRefusalCount.Should().BeNull("gone is what the delete was for");
    }

    /// <summary>
    /// A file whose digest the reconciler has not filled yet is read for one before it is sent, and
    /// the transfer records it.
    /// </summary>
    [Fact]
    public async Task AFileWithNoDigestYetIsReadForOneBeforeItIsSent()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");

        PrintFile file = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);
        file.Digest = null;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        IPrinterConnectionActor actor = ConnectAccepting();
        string expected = await PrintFileDigest.ComputeAsync(new MemoryStream("G28 ; home\n"u8.ToArray()), copyTo: null,
                                                              TestContext.Current.CancellationToken);

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        OfferedPaths(actor).Should().Equal(["/usb/queued.bgcode"]);

        context.ChangeTracker.Clear();
        (await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken))
            .Digest.Should().Be(expected, "no file is sent without one");
    }

    /// <summary>
    /// Our own copy found at another size is not adopted, and the digest recorded for its name does not
    /// follow the file to the next one - where it would vouch for whatever turned up.
    /// </summary>
    [Fact]
    public async Task OurOwnCopyAtAnotherSizeIsSentUnderTheNextNameWithoutItsDigest()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        await AddTakenCopyAsync(context);
        ConnectRefusingTransferAsExisting(existingSize: OnDiskLength + 4096);

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.DriveName.Should().Be(OwnersName);
        row.Digest.Should().BeNull("it described the bytes under the old name");
        row.ArrivedAt.Should().BeNull();
    }

    /// <summary>
    /// A transfer that ends without finishing leaves nothing under the name, so the digest it recorded
    /// goes with the path.
    /// </summary>
    [Fact]
    public async Task AnAbortedTransferForgetsTheDigestItRecorded()
    {
        // Arrange - a transfer the printer took and named, then abandoned
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        PrintFile file = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            TransferStartedAt = _clock.GetUtcNow(),
            PrinterPath = "/usb/QUEUED~1.BGC",
            DriveName = file.Name,
            Digest = SeededDigest,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await AddTransferEndAsync(PrinterEventType.TransferAborted, "/usb/queued.bgcode");
        ConnectAccepting();

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.PrinterPath.Should().BeNull("firmware removes the partial");
        row.Digest.Should().BeNull("and with it the bytes the digest described");
    }

    /// <summary>A refused transfer put nothing on the drive, so it records no digest.</summary>
    [Fact]
    public async Task ARefusedTransferRecordsNoDigest()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        ConnectRefusingTransfer(_ => ("STORAGE_FAILURE", "Failed to create directory"));

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        (await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken))
            .Digest.Should().BeNull("a digest here would vouch for whatever is under the name");
    }

    /// <summary>
    /// A print started at the panel from an older copy is not taken for the queued entry, which wants
    /// the file as it is now.
    /// </summary>
    [Fact]
    public async Task APanelPrintOfAnOlderCopyIsNotAdoptedAsTheEntry()
    {
        // Arrange - staged, overwritten since, and the printer printing the staged path
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Idle);
        await WriteFileOnDiskAsync("queued.bgcode");
        await OverwriteSeededFileAsync(context);
        await ReportAsync(context, PrinterStatus.Printing, jobId: 741);
        ConnectAnsweringJobInfo("/usb/QUEUED~1.BGC");

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        (await context.PrintJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0,
            "adopting it would record the newer file as printed");
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1,
            "the entry still wants the newer version");
    }

    /// <summary>A file sent under its owner's name is reported under that name, and is matched by it.</summary>
    [Fact]
    public async Task AFileReportIsMatchedByTheNameTheFileWasSentUnder()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        PrintFile file = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            TransferStartedAt = _clock.GetUtcNow(),
            DriveName = OwnersName,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using (TelemetryDbContext telemetry = TestTelemetryContext.For(_databasePath))
        {
            telemetry.PrinterEvents.Add(new PrinterEvent
            {
                PrinterId = PrinterId,
                Timestamp = _clock.GetUtcNow(),
                EventType = PrinterEventType.FileInfo,
                Payload = $"{{\"display_name\":\"{OwnersName}\",\"path\":\"/usb/QUEUED~2.BGC\"}}",
            });
            await telemetry.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        ConnectAccepting();

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.PrinterPath.Should().Be("/usb/QUEUED~2.BGC", "the printer names the file by the name it was sent under");
    }

    /// <summary>
    /// A transfer refused the same way every time is retried on the schedule, then held with the
    /// printer's words - and not offered again however long the hold stands.
    /// </summary>
    /// <remarks>
    /// <b>The defect, measured on hardware</b>: <c>STORAGE_FAILURE</c> with <i>"Failed to create
    /// directory"</i>, retried roughly 1 500 times at six-second intervals through two renames, a
    /// deletion and a power cycle. Six attempts, then a hold a person can read, is the replacement.
    /// </remarks>
    [Fact]
    public async Task ARefusalThatNeverChangesHoldsTheQueueAfterTheBound()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Idle);
        await WriteFileOnDiskAsync("queued.bgcode");
        TransferCounter offers = ConnectRefusingTransfer(_ => ("STORAGE_FAILURE", "Failed to create directory"));
        using QueueAdvancer advancer = NewAdvancer();

        // Act - one pass per attempt, each after its wait has run out
        for (int attempt = 1; attempt <= TransferRetryRules.HoldAfter; attempt++)
        {
            await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);
            _clock.Advance(TransferRetryRules.WaitAfter(attempt));
        }

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        offers.Count.Should().Be(TransferRetryRules.HoldAfter, "each pass after its wait is one attempt");
        row.HoldReason.Should().Be(PrintHoldReason.TransferRefused);
        row.TransferRefusalCount.Should().Be(TransferRetryRules.HoldAfter);
        row.TransferRefusalReason.Should().Be("Failed to create directory", "the printer's words are the useful part");
        row.TransferRefusalCode.Should().Be("STORAGE_FAILURE");

        PrintJob recorded = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);
        recorded.State.Should().Be(PrintState.Failed, "history gets one row, on the transition");
        recorded.Reason.Should().Be("Failed to create directory");

        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1,
            "a hold is not a cancellation");

        // And the hold stands: an hour later the file has still not been offered again.
        _clock.Advance(TimeSpan.FromHours(1));
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        offers.Count.Should().Be(TransferRetryRules.HoldAfter, "retrying is what has already failed");
        (await context.PrintJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    /// <summary>
    /// After a refusal the next pass waits out the delay rather than offering the file again at once.
    /// </summary>
    [Fact]
    public async Task ARefusedTransferIsNotOfferedAgainBeforeItsWait()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Idle);
        await WriteFileOnDiskAsync("queued.bgcode");
        TransferCounter offers = ConnectRefusingTransfer(_ => ("STORAGE_FAILURE", "Failed to create directory"));
        using QueueAdvancer advancer = NewAdvancer();

        // Act and assert
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);
        offers.Count.Should().Be(1);

        _clock.Advance(TransferRetryRules.WaitAfter(1) - TimeSpan.FromSeconds(1));
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);
        offers.Count.Should().Be(1, "the wait has not run out");

        _clock.Advance(TimeSpan.FromSeconds(1));
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);
        offers.Count.Should().Be(2, "and once it has, the file is offered again");
    }

    /// <summary>
    /// A busy transfer slot never counts, from either client, however long it lasts.
    /// </summary>
    /// <remarks>
    /// Somebody else's large transfer can hold the slot for longer than the whole retry budget, and
    /// the queue behind it only has to wait. The code-less row is the Python SDK, which sends the
    /// words and no machine reason.
    /// </remarks>
    [Theory]
    [InlineData("TRANSFER_IN_PROGRESS", "Another transfer in progress")]
    [InlineData(null, "Another transfer in progress")]
    public async Task ABusyTransferSlotNeverHoldsTheQueue(string? code, string reason)
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Idle);
        await WriteFileOnDiskAsync("queued.bgcode");
        TransferCounter offers = ConnectRefusingTransfer(_ => (code, reason));
        using QueueAdvancer advancer = NewAdvancer();
        int passes = TransferRetryRules.HoldAfter * 2;

        // Act
        for (int pass = 0; pass < passes; pass++)
        {
            await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);
            _clock.Advance(QueueAdvancer.PollInterval);
        }

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.HoldReason.Should().BeNull("a taken slot says nothing about this file");
        row.TransferRefusalCount.Should().BeNull("and so is not counted at all");
        offers.Count.Should().Be(passes, "nor spaced out - the slot may free up on the next pass");
    }

    /// <summary>
    /// A refusal that keeps changing is a situation still moving, so it keeps being retried.
    /// </summary>
    [Fact]
    public async Task ARefusalThatKeepsChangingIsNotHeld()
    {
        // Arrange - two different answers, alternating
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Idle);
        await WriteFileOnDiskAsync("queued.bgcode");
        TransferCounter offers = ConnectRefusingTransfer(attempt => attempt % 2 == 0 ?
                                                             ("STORAGE_FAILURE", "Failed to create directory") :
                                                             ("NOT_READY", "Printer not ready"));
        using QueueAdvancer advancer = NewAdvancer();
        int attempts = TransferRetryRules.HoldAfter * 2;

        // Act
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);
            _clock.Advance(TransferRetryRules.WaitAfter(1));
        }

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        offers.Count.Should().Be(attempts);
        row.HoldReason.Should().BeNull("only an unchanging answer is the signal");
        row.TransferRefusalCount.Should().Be(1, "each change starts the count over");
    }

    /// <summary>A transfer the printer finally takes forgets the refusals before it.</summary>
    [Fact]
    public async Task AnAcceptedTransferForgetsEarlierRefusals()
    {
        // Arrange - refused five times, the last one long enough ago that the wait is over
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Idle);
        await WriteFileOnDiskAsync("queued.bgcode");

        PrintFile file = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            TransferRefusalCount = TransferRetryRules.HoldAfter - 1,
            TransferRefusedAt = _clock.GetUtcNow(),
            TransferRefusalCode = "STORAGE_FAILURE",
            TransferRefusalReason = "Failed to create directory",
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        _clock.Advance(TransferRetryRules.WaitAfter(TransferRetryRules.HoldAfter - 1));
        ConnectAccepting();

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.TransferStartedAt.Should().NotBeNull("the transfer is under way");
        row.TransferRefusalCount.Should().BeNull("a later failure must start from one, not from five");
        row.TransferRefusalReason.Should().BeNull();
    }

    /// <summary>
    /// A printer that refuses every transfer offer with whatever <paramref name="answer"/> gives for
    /// that attempt, counting the offers.
    /// </summary>
    /// <param name="answer">The machine reason and words for the zero-based attempt number.</param>
    private TransferCounter ConnectRefusingTransfer(Func<int, (string? code, string? reason)> answer)
    {
        TransferCounter counter = new();
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.IsOpen.Returns(true);

        actor.SendCommandAsync(Arg.Any<ISendableCommand>(), Arg.Any<CancellationToken>())
             .Returns(call =>
             {
                 if (call.Arg<ISendableCommand>() is not (StartConnectDownload or StartEncryptedDownload))
                 {
                     // The free-space question: refused, which the loop reads as "unknown, so room".
                     return Task.FromResult(new CommandSendResult(CommandSendOutcome.Completed,
                                                                  new CommandOutcome(PrinterEventType.Rejected, null)));
                 }

                 (string? code, string? reason) = answer(counter.Count++);

                 return Task.FromResult(new CommandSendResult(CommandSendOutcome.Completed,
                                                              new CommandOutcome(PrinterEventType.Rejected, reason)
                                                                  { MachineReason = code }));
             });

        _registry.Register(PrinterId, actor, overPlaintext: false);

        return counter;
    }

    /// <summary>How many transfer offers a printer has been sent.</summary>
    private sealed class TransferCounter
    {
        public int Count { get; set; }
    }

    /// <summary>
    /// A printer that never answers a <c>START_PRINT</c>: the entry stays queued <b>and</b> the row
    /// records that we asked.
    /// </summary>
    /// <remarks>
    /// <b>The defect this whole path exists for, at the moment it happens</b> (hardware,
    /// 2026-08-21). The loop used to catch the timeout beside the transient failures - "the next tick
    /// asks again" - and write nothing at all, so the printer got on with the print while the queue
    /// went on holding an entry for it. Both halves of the assertion matter: without the entry the
    /// print would be silently dropped if the command never landed, and without the row nothing knows
    /// there is a question to answer.
    /// </remarks>
    [Fact]
    public async Task APrintCommandThatIsNeverAnsweredLeavesBothTheEntryAndAQuestion()
    {
        // Arrange - the file is on the drive and the printer is ready, so the loop will print
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
        ConnectTimingOutOnPrint();

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1,
            "the command may not have landed, and dropping the entry would lose the print");

        PrintJob commanded = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);
        commanded.State.Should().Be(PrintState.Unconfirmed);
        commanded.EndedAt.Should().BeNull("nothing has ended - nothing is known");
        commanded.PrinterPath.Should().Be("/usb/QUEUED~1.BGC", "what was asked for is what the answer is matched against");
    }

    /// <summary>
    /// The printer names our file, so the print was ours all along: it is adopted and the entry is
    /// consumed.
    /// </summary>
    /// <remarks>
    /// <b>This is the case the timeout was hiding.</b> The printer did not answer <i>because</i> it
    /// accepted the command and went off to home and heat, so the print was running the whole time.
    /// Adoption is what stops the entry printing a second time later - and it is done on the
    /// printer's own answer, never on the status, because a status can say a printer is printing but
    /// never whose print it is.
    /// </remarks>
    [Fact]
    public async Task APrintTheTimedOutCommandStartedIsAdopted()
    {
        // Arrange - the command has gone unanswered, and the printer is now reporting our print
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
        ConnectTimingOutOnPrint();

        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        await ReportAsync(context, PrinterStatus.Printing, jobId: 724);
        ConnectAnsweringJobInfo("/usb/QUEUED~1.BGC");

        // Act
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob adopted = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        adopted.State.Should().Be(PrintState.Printing,
                                  "the telemetry that identified the print is the telemetry that says it is printing");
        adopted.FirmwareJobId.Should().Be(724, "the two id spaces are mapped here or not at all");
        adopted.PrintUuid.Should().Be(QueuedPrintUuid, "the intention and the print it produced stay connected");

        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0,
            "now - and only now - has the entry done its job");
    }

    /// <summary>
    /// The same, for a file staged under its owner's name: the printer describes the job by that name,
    /// not the file's own, and it is still ours.
    /// </summary>
    [Fact]
    public async Task APrintTheTimedOutCommandStartedIsRecognisedByTheNameTheFileWasSentUnder()
    {
        // Arrange - staged under its owner's name; the job answer names it that way, by another path
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
        PrintFileOnPrinter staged = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);
        staged.DriveName = OwnersName;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        ConnectTimingOutOnPrint();

        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        await ReportAsync(context, PrinterStatus.Printing, jobId: 726);
        ConnectAnsweringJobInfo("/usb/OTHER~9.BGC", OwnersName);

        // Act
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob adopted = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        adopted.FirmwareJobId.Should().Be(726, "the name the printer uses is the one the file was sent under");
        adopted.FileName.Should().Be("queued.bgcode", "history keeps the file's own name");
    }

    /// <summary>
    /// The printer says it has no job, so the command really was ignored: the question is dropped and
    /// the print is queued as before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Removed rather than closed as failed, because nothing failed.</b> A command went unanswered
    /// and the printer turned out never to have acted on it, which is not a print and has no place in
    /// a history of prints. The entry stays, so the queue simply asks again - which is what the old
    /// catch block was right about, for the case it was wrong to assume.
    /// </para>
    /// <para>
    /// <b>And the retry happens in the same pass</b>, which is why this asserts on the row's identity
    /// rather than on how many there are: resolving to "it never started" leaves a ready printer with
    /// a queue, so the rules command it again immediately and a fresh question takes the old one's
    /// place. Counting rows would pass whether or not the first was ever removed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APrintTheCommandNeverStartedIsRetriedRatherThanRecorded()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
        ConnectTimingOutOnPrint();

        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        context.ChangeTracker.Clear();
        long unanswered = (await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken)).Id;

        // The printer goes on saying it is ready and empty-handed, past the point where it could still
        // be getting started.
        _clock.Advance(QueueAdvancer.StartUnconfirmedGrace + TimeSpan.FromSeconds(1));
        await ReportAsync(context, PrinterStatus.Ready, jobId: null);

        // Act
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        (await context.PrintJobs.AnyAsync(job => job.Id == unanswered, TestContext.Current.CancellationToken))
            .Should().BeFalse("a print that never happened is not history");

        (await context.PrintJobs.AnyAsync(job => job.EndedAt != null, TestContext.Current.CancellationToken))
            .Should().BeFalse("nothing failed - a question went unanswered for a minute");

        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1,
            "somebody still wants this printed");

        // And when the printer does answer, it prints - once. The fresh question the retry raised has
        // to age out the same way, which is deliberate: each attempt is judged on the printer's own
        // reports since that attempt, so a machine that swallows commands is asked again on a clock
        // rather than on every tick.
        ConnectAccepting();
        _clock.Advance(QueueAdvancer.StartUnconfirmedGrace + TimeSpan.FromSeconds(1));
        await ReportAsync(context, PrinterStatus.Ready, jobId: null);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        context.ChangeTracker.Clear();
        (await context.PrintJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    /// <summary>
    /// A print that turns out to be somebody else's is not adopted, and does not consume our entry.
    /// </summary>
    /// <remarks>
    /// <b>The mirror failure, and the reason the loop asks rather than reading the status.</b>
    /// A printer that is printing is not evidence of anything: somebody may have started a job at the
    /// panel in the same window. Adopting on the status alone would delete a queue entry and attach
    /// its history to a stranger's print.
    /// </remarks>
    [Fact]
    public async Task APrintThatIsSomebodyElsesIsNotAdopted()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
        ConnectTimingOutOnPrint();

        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        await ReportAsync(context, PrinterStatus.Printing, jobId: 725);
        ConnectAnsweringJobInfo("/usb/SOMEON~1.BGC");

        // Act
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        (await context.PrintJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0,
            "our command did not start what is running, so there is no print of ours to record");
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1,
            "the entry waits for the printer like any other");
    }

    /// <summary>
    /// A printer that reports a job and will not describe it is eventually given up on - and the
    /// queue holds rather than guessing.
    /// </summary>
    /// <remarks>
    /// <b>Both halves are the answer, and either alone is wrong.</b> Closing the row without holding
    /// would let the queue advance onto a print that may already have run, which is the original
    /// defect with a quarter of an hour in front of it; holding without closing would leave the
    /// printer's one open-print slot occupied for ever. The bound exists because waiting for days on
    /// a connected printer is not an answer (Henrik, 2026-08-22).
    /// </remarks>
    [Fact]
    public async Task APrinterThatWillNotSayIsGivenUpOnAndTheQueueHolds()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
        ConnectTimingOutOnPrint();

        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Printing something, and refusing every question about it.
        await ReportAsync(context, PrinterStatus.Printing, jobId: 726);
        ConnectTimingOutOnPrint();
        _clock.Advance(QueueAdvancer.StartUnresolvableAfter + TimeSpan.FromMinutes(1));

        // Act
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob given = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        given.State.Should().Be(PrintState.Unknown, "it stopped being observable without saying how");
        given.EndedAt.Should().NotBeNull("the open-print slot cannot be held for ever");

        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);
        row.HoldReason.Should().Be(PrintHoldReason.PrintStartUnresolved,
                                   "advancing might print the file twice, so a person decides");

        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1,
            "a hold is not a cancellation");
    }

    /// <summary>
    /// <c>"No job in progress"</c> in answer to a <c>START_PRINT</c> settles nothing: the row stays
    /// open as a question and the entry stays queued.
    /// </summary>
    /// <remarks>
    /// <b>The rejection that is not a refusal.</b> Firmware renders the ack against its momentary
    /// state, and a print it has accepted passes through a state with no job before it reports
    /// <c>PRINTING</c> - so this arrives, command id and all, for a print that is starting. Routing
    /// it through the transient arm - which removes the row - is how a phantom print is minted: the
    /// print runs with no record, and the entry survives to print the file a second time.
    /// </remarks>
    [Fact]
    public async Task ANoJobInProgressRefusalLeavesTheQuestionOpen()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
        ConnectRefusing("No job in progress");

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob question = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        question.State.Should().Be(PrintState.Unconfirmed, "the answer said nothing about whether the print is running");
        question.EndedAt.Should().BeNull();

        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1,
            "consuming the entry on a rejection that lies would drop the print; removing the row would print it twice");
    }

    /// <summary>
    /// And once the printer reports the print and describes it as ours, the falsely rejected print is
    /// adopted through the same resolution a timeout takes.
    /// </summary>
    [Fact]
    public async Task AFalselyRejectedPrintIsAdoptedOnceThePrinterDescribesIt()
    {
        // Arrange - the false rejection has been received, and the printer is now printing our file
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
        ConnectRefusing("No job in progress");

        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        await ReportAsync(context, PrinterStatus.Printing, jobId: 736);
        ConnectAnsweringJobInfo("/usb/QUEUED~1.BGC");

        // Act
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob adopted = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        adopted.State.Should().Be(PrintState.Printing);
        adopted.FirmwareJobId.Should().Be(736);
        adopted.CommandedAt.Should().NotBeNull("this print was commanded - the rejection lied, the command did not");

        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    /// <summary>
    /// A print begun at the printer, of a file this loop staged for a still-queued entry, is adopted:
    /// a row opens for it and the entry is consumed rather than surviving to print a second time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No command of ours is involved anywhere in this test</b>, which is what distinguishes it
    /// from the timed-out and falsely-rejected cases above: a staged file is also the panel's offer -
    /// firmware opens its one-click preview for a file that arrives over the wire - so the person at
    /// the machine can start exactly the file the loop was about to command, and always wins the
    /// race, being behind a button rather than a poll.
    /// </para>
    /// <para>
    /// <c>CommandedAt</c> stays null on the adopted row: that is the record that the printer, not a
    /// command of ours, started this print - the start-side sibling of <c>StoppedByUserId</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APanelPrintOfAStagedFileIsAdoptedWithoutACommand()
    {
        // Arrange - the file is staged, nothing was commanded, and the printer starts printing it
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Idle);
        await ReportAsync(context, PrinterStatus.Printing, jobId: 737);
        ConnectAnsweringJobInfo("/usb/QUEUED~1.BGC");

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob adopted = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        adopted.State.Should().Be(PrintState.Printing);
        adopted.FirmwareJobId.Should().Be(737);
        adopted.PrintUuid.Should().Be(QueuedPrintUuid, "the intention and the print it produced stay connected");
        adopted.QueuedByUserId.Should().Be(1);
        adopted.PrinterPath.Should().Be("/usb/QUEUED~1.BGC");
        adopted.CommandedAt.Should().BeNull("no command of ours started this print, and null is that record");

        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0,
            "the entry surviving is what used to print the file a second time");
    }

    /// <summary>
    /// A panel print of a file sent under its owner's name is recognised by that name, since that is
    /// what the printer calls it.
    /// </summary>
    [Fact]
    public async Task APanelPrintOfAFileSentUnderItsOwnersNameIsAdopted()
    {
        // Arrange - staged under its owner's name; the printer's job names it that way, by another path
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Idle);
        PrintFileOnPrinter staged = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);
        staged.DriveName = OwnersName;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await ReportAsync(context, PrinterStatus.Printing, jobId: 739);
        ConnectAnsweringJobInfo("/usb/OTHER~9.BGC", OwnersName);

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        (await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken)).FirmwareJobId.Should().Be(739);
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    /// <summary>
    /// A panel print of something nobody queued is asked about once, left alone, and not asked about
    /// again - a stranger's print must not cost a question per pass for its whole duration.
    /// </summary>
    [Fact]
    public async Task APanelPrintOfSomethingElseIsAskedAboutOnceAndLeftAlone()
    {
        // Arrange - our file is staged, but what the printer is running is not it
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Idle);
        await ReportAsync(context, PrinterStatus.Printing, jobId: 738);
        IPrinterConnectionActor actor = ConnectAnsweringJobInfo("/usb/ALIEN~1.BGC");

        // Act - several passes, as a long print would see
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        (await context.PrintJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0,
            "a running job whose path nothing here wrote is not ours to record");
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1,
            "the entry waits for the printer like any other");

        await actor.Received(1).SendCommandAsync(Arg.Any<ISendableCommand>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Nothing is adopted from a printer in <c>Attention</c>: a job id is already on the wire during
    /// the preview's own questions, while the person can still back out of the print entirely.
    /// </summary>
    [Fact]
    public async Task NoPanelPrintIsAdoptedFromAttention()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Idle);
        await ReportAsync(context, PrinterStatus.Attention, jobId: 739);
        IPrinterConnectionActor actor = ConnectAnsweringJobInfo("/usb/QUEUED~1.BGC");

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert - not even asked: adopting there would consume the entry for a print that may never run
        context.ChangeTracker.Clear();
        (await context.PrintJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);

        await actor.DidNotReceive().SendCommandAsync(Arg.Any<ISendableCommand>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A mid-print <c>Busy</c> excursion does not end the print - the row stays open and is
    /// promoted back to its ordinary life when the printer reports printing again.
    /// </summary>
    /// <remarks>
    /// <b>Hardware evidence, observed live 2026-08-28.</b> A filament runout on an MK3.5 opened
    /// with ~8 seconds of <c>BUSY</c> carrying no job id before settling into <c>ATTENTION</c>,
    /// and the close rule read that as "stopped printing without saying how": the row closed
    /// <c>Unknown</c> mid-print while the printer went on to finish the file - a running print
    /// with no open row, which is the printer page's "printing with no filename" symptom.
    /// </remarks>
    [Fact]
    public async Task AMidPrintBusyExcursionDoesNotEndThePrint()
    {
        // Arrange - an open print, and the printer momentarily reporting BUSY with no job id
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Printing);

        context.QueuedPrints.RemoveRange(context.QueuedPrints);
        context.PrintJobs.Add(new PrintJob
        {
            PrinterId = PrinterId,
            FileName = "runout.bgcode",
            QueuedByUserId = 1,
            StartedAt = _clock.GetUtcNow(),
            CommandedAt = _clock.GetUtcNow(),
            FirmwareJobId = 5,
            State = PrintState.Printing,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await ReportAsync(context, PrinterStatus.Busy, jobId: null);

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert - still the active print
        context.ChangeTracker.Clear();
        PrintJob active = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);
        active.EndedAt.Should().BeNull("a busy machine is not a machine that stopped printing");
        active.State.Should().Be(PrintState.Printing);

        // And when the excursion passes, life continues as if nothing happened.
        await ReportAsync(context, PrinterStatus.Printing, jobId: 5);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        context.ChangeTracker.Clear();
        (await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken)).EndedAt.Should().BeNull();
    }

    /// <summary>
    /// An open print on a printer this process has not heard from is not ended.
    /// </summary>
    /// <remarks>
    /// The restart case. With telemetry held in memory the store starts empty, the queue's first pass
    /// runs seconds after listening, and a printer takes seconds to minutes to reconnect - so the pass
    /// always sees no live state, and reading that as "stopped printing" closed the row every time.
    /// </remarks>
    [Fact]
    public async Task AnOpenPrintOnAPrinterNotYetHeardFromIsNotEnded()
    {
        // Arrange - an open print, no live state, nothing connected
        await using HomespoolDbContext context = await OpenPrintAsync(PrinterStatus.Printing);

        context.PrinterLiveStates.RemoveRange(context.PrinterLiveStates);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        await ShouldStillBePrintingAsync(context, "a printer that has said nothing has not said its print ended");
    }

    /// <summary>
    /// A printer that has connected but not yet reported is still not heard from.
    /// </summary>
    /// <remarks>
    /// The seconds between the socket opening and the first sample being stored. Connectivity is
    /// not the question; what the printer has said is.
    /// </remarks>
    [Fact]
    public async Task AnOpenPrintWaitsThroughTheGapBetweenConnectingAndTheFirstReport()
    {
        // Arrange - connected, but no live state yet
        await using HomespoolDbContext context = await OpenPrintAsync(PrinterStatus.Printing);

        context.PrinterLiveStates.RemoveRange(context.PrinterLiveStates);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        ConnectAccepting();

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        await ShouldStillBePrintingAsync(context, "a socket is not a report");
    }

    /// <summary>
    /// A status left over from before this process started does not end a print.
    /// </summary>
    /// <remarks>
    /// Live state can be older than the process - seeded at startup from what a shutdown saved, which
    /// is days old if a more recent save failed. An <c>Idle</c> from then says nothing about the print
    /// opened since, and "no live state at all" would not catch it.
    /// </remarks>
    [Fact]
    public async Task AStatusLeftOverFromBeforeStartupDoesNotEndAPrint()
    {
        // Arrange - the printer last said Idle, and then the process restarted
        await using HomespoolDbContext context = await OpenPrintAsync(PrinterStatus.Idle);

        _clock.Advance(TimeSpan.FromMinutes(5));

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        await ShouldStillBePrintingAsync(context, "an old report is not the printer speaking about now");
    }

    /// <summary>
    /// A printer that reported <c>Stopped</c> and then went away has said how its print ended, and the
    /// row closes on it.
    /// </summary>
    /// <remarks>
    /// Why the rule is what the printer has said rather than whether it is connected: cancelled at
    /// the panel and switched off before the next pass, it still told us.
    /// </remarks>
    [Fact]
    public async Task APrinterThatSaidStoppedAndThenWentAwayIsRecordedAsStopped()
    {
        // Arrange
        await using HomespoolDbContext context = await OpenPrintAsync(PrinterStatus.Printing);
        using QueueAdvancer advancer = NewAdvancer();

        _clock.Advance(TimeSpan.FromSeconds(2));
        await ReportAsync(context, PrinterStatus.Stopped, jobId: 790);

        // Act - and nothing is connected
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);
        job.State.Should().Be(PrintState.Stopped, "the printer said so before it went");
        job.EndedAt.Should().NotBeNull();
    }

    /// <summary>
    /// An <c>Unknown</c> the printer actually reported still closes the row, so the wait is not read as
    /// "Unknown never closes".
    /// </summary>
    [Fact]
    public async Task AnUnknownThePrinterReportsStillEndsThePrint()
    {
        // Arrange
        await using HomespoolDbContext context = await OpenPrintAsync(PrinterStatus.Printing);
        using QueueAdvancer advancer = NewAdvancer();

        _clock.Advance(TimeSpan.FromSeconds(2));
        await ReportAsync(context, PrinterStatus.Unknown, jobId: null);

        // Act
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);
        job.State.Should().Be(PrintState.Unknown, "a reported status is a report, whatever its value");
        job.EndedAt.Should().NotBeNull();
    }

    /// <summary>
    /// A print that ended while nobody here was listening is settled by asking the printer, not
    /// recorded as <c>Unknown</c>.
    /// </summary>
    /// <remarks>
    /// The printer comes back already <c>Ready</c>, its Finished screen dismissed, which says it
    /// stopped printing and not how. Firmware keeps the outcome of its last two jobs.
    /// </remarks>
    [Fact]
    public async Task APrintThatEndedWhileNobodyWasListeningIsSettledByAskingThePrinter()
    {
        // Arrange - the printer reconnects Ready and remembers the job finishing
        await using HomespoolDbContext context = await OpenPrintAsync(PrinterStatus.Printing);
        ConnectRememberingJobOutcome("FIN_OK");
        using QueueAdvancer advancer = NewAdvancer();

        _clock.Advance(TimeSpan.FromSeconds(2));
        await ReportAsync(context, PrinterStatus.Ready, jobId: null);

        // Act
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);
        job.State.Should().Be(PrintState.Finished, "the printer remembered how it ended");
    }

    /// <summary>
    /// A print begins when the filament odometer first rises past the reading it opened with, not when
    /// the printer says <c>PRINTING</c> - and what it extruded is the reading at the end less that one.
    /// </summary>
    /// <remarks>
    /// The MK3.5's shape: <c>PRINTING</c> from the first report with the nozzle cold, minutes of homing,
    /// probing and heating, and a retraction on the way that dips the reading below where it started.
    /// A dip is still "not yet", which is why the rule is a rise above the opening reading and not a
    /// change from it.
    /// </remarks>
    [Fact]
    public async Task APrintBeginsWhenItsFilamentFirstRisesAndRecordsWhatItUsed()
    {
        // Arrange - printing, and the first reading of this print heard
        await using HomespoolDbContext context = await OpenPrintAsync(PrinterStatus.Printing);
        using QueueAdvancer advancer = NewAdvancer();
        DateTimeOffset started = _clock.GetUtcNow();

        _clock.Advance(TimeSpan.FromSeconds(5));
        await ReportAsync(context, PrinterStatus.Printing, jobId: 790, filament: 1000f);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Warm-up: a retraction below the opening reading, and back to it
        _clock.Advance(TimeSpan.FromSeconds(100));
        await ReportAsync(context, PrinterStatus.Printing, jobId: 790, filament: 998f);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        _clock.Advance(TimeSpan.FromSeconds(60));
        await ReportAsync(context, PrinterStatus.Printing, jobId: 790, filament: 1000f);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        context.ChangeTracker.Clear();
        PrintJob warming = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);
        warming.FilamentAtStart.Should().Be(1000f);
        warming.BegunAt.Should().BeNull("nothing has left the nozzle yet, whatever the status says");

        // Act - plastic
        _clock.Advance(TimeSpan.FromSeconds(8));
        DateTimeOffset firstPlastic = _clock.GetUtcNow();
        await ReportAsync(context, PrinterStatus.Printing, jobId: 790, filament: 1003.5f);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        _clock.Advance(TimeSpan.FromMinutes(13));
        await ReportAsync(context, PrinterStatus.Printing, jobId: 790, filament: 1500f);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        _clock.Advance(TimeSpan.FromSeconds(5));
        await ReportAsync(context, PrinterStatus.Finished, jobId: null);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);
        job.State.Should().Be(PrintState.Finished);
        job.BegunAt.Should().Be(firstPlastic);
        (job.BegunAt - started).Should().Be(TimeSpan.FromSeconds(173));
        job.FilamentAtEnd.Should().Be(1500f, "the last reading heard during the print, which a finished screen no longer sends");
        job.FilamentUsed.Should().Be(500f);
    }

    /// <summary>
    /// The reading standing when a print is commanded is the previous print's, and is not taken as
    /// this one's opening reading.
    /// </summary>
    /// <remarks>
    /// Firmware sends the odometer only while it has a job, so between prints the live value is
    /// whatever the last print said. Anything that moved the extruder since would read, once this
    /// print reported, as the first plastic.
    /// </remarks>
    [Fact]
    public async Task AReadingLeftFromThePreviousPrintIsNotTheOpeningOne()
    {
        // Arrange - a reading this run heard, before the print was opened
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Printing);
        using QueueAdvancer advancer = NewAdvancer();

        _clock.Advance(TimeSpan.FromSeconds(1));
        await ReportAsync(context, PrinterStatus.Idle, jobId: null, filament: 900f);

        _clock.Advance(TimeSpan.FromSeconds(30));
        context.QueuedPrints.RemoveRange(context.QueuedPrints);
        context.PrintJobs.Add(new PrintJob
        {
            PrinterId = PrinterId,
            FileName = "frame.bgcode",
            QueuedByUserId = 1,
            StartedAt = _clock.GetUtcNow(),
            CommandedAt = _clock.GetUtcNow(),
            FirmwareJobId = 790,
            State = PrintState.Printing,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        _clock.Advance(TimeSpan.FromSeconds(2));
        await ReportAsync(context, PrinterStatus.Printing, jobId: 790);

        // Act
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        context.ChangeTracker.Clear();
        (await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken)).FilamentAtStart
            .Should().BeNull("900 was heard before this print existed");

        _clock.Advance(TimeSpan.FromSeconds(3));
        await ReportAsync(context, PrinterStatus.Printing, jobId: 790, filament: 912f);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);
        job.FilamentAtStart.Should().Be(912f, "the first reading this print reported");
        job.BegunAt.Should().BeNull("a reading that differs from the previous print's is not plastic moving");
    }

    /// <summary>
    /// A reading that rose while this process was not running is not written as when the print began.
    /// </summary>
    /// <remarks>
    /// The first pass after a restart would otherwise record the restart as the first extrusion. Null
    /// is the honest answer: the moment happened and nobody here saw it.
    /// </remarks>
    [Fact]
    public async Task AFilamentRiseNobodyHereSawIsNotWhenThePrintBegan()
    {
        // Arrange - the opening reading taken by the run before this one, and still standing in live
        // state from then
        await using HomespoolDbContext context = await OpenPrintAsync(PrinterStatus.Printing);

        PrintJob opened = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);
        opened.FilamentAtStart = 1000f;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        _clock.Advance(TimeSpan.FromSeconds(5));
        await ReportAsync(context, PrinterStatus.Printing, jobId: 790, filament: 1000f);

        _clock.Advance(TimeSpan.FromMinutes(4));
        using QueueAdvancer restarted = NewAdvancer();

        // A pass on a report that carries no reading: the 1000 standing is the old run's, and is not
        // this run seeing the print before it moved.
        _clock.Advance(TimeSpan.FromSeconds(5));
        await ReportAsync(context, PrinterStatus.Printing, jobId: 790);
        await restarted.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Act - the first reading this run hears is already past it, and then more
        _clock.Advance(TimeSpan.FromSeconds(5));
        await ReportAsync(context, PrinterStatus.Printing, jobId: 790, filament: 1080f);
        await restarted.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        _clock.Advance(TimeSpan.FromSeconds(5));
        await ReportAsync(context, PrinterStatus.Printing, jobId: 790, filament: 1090f);
        await restarted.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);
        job.BegunAt.Should().BeNull("the rise happened while nothing here was listening");
        job.FilamentAtStart.Should().Be(1000f, "the opening reading is still good for what the print used");
    }

    /// <summary>
    /// A print opened before this process started gets no opening reading from after the restart.
    /// </summary>
    /// <remarks>
    /// The first report after a restart may already be past the first extrusion, and an opening reading
    /// taken there would make the very next rise look like the start - and undercount what it used.
    /// </remarks>
    [Fact]
    public async Task APrintOpenedBeforeARestartTakesNoOpeningReadingAfterIt()
    {
        // Arrange
        await using HomespoolDbContext context = await OpenPrintAsync(PrinterStatus.Printing);

        _clock.Advance(TimeSpan.FromMinutes(2));
        using QueueAdvancer restarted = NewAdvancer();

        // Act
        _clock.Advance(TimeSpan.FromSeconds(5));
        await ReportAsync(context, PrinterStatus.Printing, jobId: 790, filament: 1000f);
        await restarted.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        _clock.Advance(TimeSpan.FromSeconds(5));
        await ReportAsync(context, PrinterStatus.Printing, jobId: 790, filament: 1010f);
        await restarted.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);
        job.FilamentAtStart.Should().BeNull();
        job.BegunAt.Should().BeNull();
    }

    /// <summary>A print adopted from the panel was noticed, not started, and gets no filament readings.</summary>
    [Fact]
    public async Task APanelPrintGetsNoFilamentReadings()
    {
        // Arrange - no command of ours started it
        await using HomespoolDbContext context = await OpenPrintAsync(PrinterStatus.Printing);

        PrintJob adopted = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);
        adopted.CommandedAt = null;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using QueueAdvancer advancer = NewAdvancer();

        // Act
        _clock.Advance(TimeSpan.FromSeconds(5));
        await ReportAsync(context, PrinterStatus.Printing, jobId: 790, filament: 1000f);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        _clock.Advance(TimeSpan.FromSeconds(5));
        await ReportAsync(context, PrinterStatus.Printing, jobId: 790, filament: 1010f);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);
        job.FilamentAtStart.Should().BeNull("the first extrusion may have passed before the print was noticed");
        job.BegunAt.Should().BeNull();
    }

    /// <summary>
    /// An ending the printer did not state records no closing reading, so what the print used is left
    /// unknown rather than recorded short.
    /// </summary>
    [Fact]
    public async Task AnEndingThePrinterDidNotStateRecordsNoClosingReading()
    {
        // Arrange - an opening reading and some plastic
        await using HomespoolDbContext context = await OpenPrintAsync(PrinterStatus.Printing);
        using QueueAdvancer advancer = NewAdvancer();

        _clock.Advance(TimeSpan.FromSeconds(5));
        await ReportAsync(context, PrinterStatus.Printing, jobId: 790, filament: 1000f);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        _clock.Advance(TimeSpan.FromSeconds(5));
        await ReportAsync(context, PrinterStatus.Printing, jobId: 790, filament: 1200f);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Act - it stopped printing without saying how, and nothing is connected to ask
        _clock.Advance(TimeSpan.FromSeconds(5));
        await ReportAsync(context, PrinterStatus.Unknown, jobId: null);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);
        job.EndedAt.Should().NotBeNull();
        job.BegunAt.Should().NotBeNull();
        job.FilamentAtEnd.Should().BeNull("how much was printed after the last reading nobody heard");
        job.FilamentUsed.Should().BeNull();
    }

    /// <summary>One open print on the test printer, with the firmware job id and authority a promoted row carries.</summary>
    private async Task<HomespoolDbContext> OpenPrintAsync(PrinterStatus status)
    {
        HomespoolDbContext context = await SeedAsync(arrived: true, status: status);

        context.QueuedPrints.RemoveRange(context.QueuedPrints);
        context.PrintJobs.Add(new PrintJob
        {
            PrinterId = PrinterId,
            FileName = "frame.bgcode",
            QueuedByUserId = 1,
            QueuedByScope = CapabilitySet.Format(CapabilitySet.Everything),
            StartedAt = _clock.GetUtcNow(),
            CommandedAt = _clock.GetUtcNow(),
            FirmwareJobId = 790,
            State = PrintState.Printing,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return context;
    }

    private static async Task ShouldStillBePrintingAsync(HomespoolDbContext context, string because)
    {
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        job.EndedAt.Should().BeNull(because);
        job.State.Should().Be(PrintState.Printing);
    }

    /// <summary>Overwrites what the printer is last known to have said.</summary>
    /// <remarks>
    /// <c>LastSeenAt</c> moves with it, deliberately: a live state that has not been refreshed since
    /// the command is not an answer about it, and several rules turn on exactly that. A
    /// <paramref name="filament"/> reading is heard now too; without one, whatever reading was
    /// standing stays, with the time it was heard, as a message not carrying the field leaves it.
    /// </remarks>
    private async Task ReportAsync(HomespoolDbContext context, PrinterStatus status, int? jobId, float? filament = null)
    {
        context.ChangeTracker.Clear();

        PrinterLiveState live = await context.PrinterLiveStates
                                             .SingleAsync(state => state.PrinterId == PrinterId,
                                                          TestContext.Current.CancellationToken);

        live.Status = status;
        live.JobId = jobId;
        live.LastSeenAt = _clock.GetUtcNow();

        if (filament is not null)
        {
            live.FilamentUsed = filament;
            live.FilamentUsedAt = _clock.GetUtcNow();
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A printer that takes a print and never acknowledges it - the printer of 2026-08-21, which was
    /// slow precisely because it had accepted the command.
    /// </summary>
    /// <remarks>
    /// It answers nothing at all, so it stands in for the questions afterwards going unanswered too.
    /// </remarks>
    private void ConnectTimingOutOnPrint()
    {
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.IsOpen.Returns(true);
        actor.SendAsync(Arg.Any<IPrinterIntent>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new CommandSendResult(CommandSendOutcome.ResponseTimedOut, null)));
        actor.SendCommandAsync(Arg.Any<ISendableCommand>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new CommandSendResult(CommandSendOutcome.ResponseTimedOut, null)));

        _registry.Register(PrinterId, actor, overPlaintext: false);
    }

    /// <summary>
    /// A printer that no longer runs the job asked about, but still remembers how it ended -
    /// <c>FIN_OK</c> or <c>FIN_STOPPED</c>, which is all its two-job history holds.
    /// </summary>
    private IPrinterConnectionActor ConnectRememberingJobOutcome(string finState)
    {
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.IsOpen.Returns(true);
        actor.SendAsync(Arg.Any<IPrinterIntent>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new CommandSendResult(CommandSendOutcome.ResponseTimedOut, null)));
        actor.SendCommandAsync(Arg.Any<ISendableCommand>(), Arg.Any<CancellationToken>())
             .Returns(call => call.Arg<ISendableCommand>() is SendJobInfo ?
                          Task.FromResult(new CommandSendResult(
                                              CommandSendOutcome.Completed,
                                              new CommandOutcome(PrinterEventType.JobInfo, null),
                                              JsonSerializer.Deserialize<JsonElement>(
                                                  $"{{\"state\":\"{finState}\"}}"))) :
                          Task.FromResult(new CommandSendResult(CommandSendOutcome.ResponseTimedOut, null)));

        _registry.Register(PrinterId, actor, overPlaintext: false);

        return actor;
    }

    /// <summary>
    /// A print running at the panel while nothing of ours is on the drive cannot be ours, so the
    /// printer is not asked about it at all.
    /// </summary>
    [Fact]
    public async Task APanelPrintWithNothingStagedIsNotAskedAbout()
    {
        // Arrange - queued, but never sent, so no path of ours exists for the job to match
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Idle);
        await WriteFileOnDiskAsync("queued.bgcode");
        await ReportAsync(context, PrinterStatus.Printing, jobId: 740);
        IPrinterConnectionActor actor = ConnectAnsweringJobInfo("/usb/QUEUED~1.BGC");

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        await actor.DidNotReceive().SendCommandAsync(Arg.Any<SendJobInfo>(), Arg.Any<CancellationToken>());

        context.ChangeTracker.Clear();
        (await context.PrintJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    /// <summary>
    /// A question about a panel print that goes unanswered is asked again on the next pass, since
    /// nothing was learnt from it.
    /// </summary>
    [Theory]
    [InlineData(CommandSendOutcome.NotConnected)]
    [InlineData(CommandSendOutcome.AlreadyInFlight)]
    [InlineData(CommandSendOutcome.ResponseTimedOut)]
    [InlineData(CommandSendOutcome.SendTimedOut)]
    public async Task AnUnansweredQuestionAboutAPanelPrintIsAskedAgain(CommandSendOutcome outcome)
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Idle);
        await ReportAsync(context, PrinterStatus.Printing, jobId: 741);
        IPrinterConnectionActor actor = ConnectAnswering(_ => Unanswered(outcome));

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        await actor.Received(2).SendCommandAsync(Arg.Any<SendJobInfo>(), Arg.Any<CancellationToken>());

        context.ChangeTracker.Clear();
        (await context.PrintJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    /// <summary>
    /// A panel print the printer refuses to describe is asked about once - except when the refusal is
    /// "No job in progress", which is what a print that is only just starting sounds like.
    /// </summary>
    /// <param name="reason">The printer's refusal.</param>
    /// <param name="asked">How many of two passes should ask.</param>
    [Theory]
    [InlineData("No job in progress", 2)]
    [InlineData("Job ID doesn't match", 1)]
    public async Task APanelPrintThePrinterWillNotDescribeIsAskedAboutAgainOnlyWhileStarting(string reason, int asked)
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Idle);
        await ReportAsync(context, PrinterStatus.Printing, jobId: 742);
        IPrinterConnectionActor actor = ConnectAnswering(_ => Answered(PrinterEventType.Rejected, reason));

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        await actor.Received(asked).SendCommandAsync(Arg.Any<SendJobInfo>(), Arg.Any<CancellationToken>());

        context.ChangeTracker.Clear();
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    /// <summary>
    /// A panel print described without a name settles nothing, and a current job always carries one,
    /// so it is not asked about again.
    /// </summary>
    [Fact]
    public async Task APanelPrintDescribedWithoutANameIsAskedAboutOnce()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Idle);
        await ReportAsync(context, PrinterStatus.Printing, jobId: 743);
        IPrinterConnectionActor actor = ConnectAnswering(_ => Answered(PrinterEventType.JobInfo, json: "{\"state\":\"PRINTING\"}"));

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        await actor.Received(1).SendCommandAsync(Arg.Any<SendJobInfo>(), Arg.Any<CancellationToken>());

        context.ChangeTracker.Clear();
        (await context.PrintJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0,
            "nothing named the job, so nothing can claim it");
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    /// <summary>
    /// After an unanswered start, only "No job in progress" past the grace period says the print never
    /// began; any other refusal, or a job described without a name, leaves the question open.
    /// </summary>
    /// <param name="reason">The printer's refusal, or null when it answers.</param>
    /// <param name="json">What it answers with, when it does.</param>
    /// <param name="neverStarted">Whether the unanswered start is settled as never having happened.</param>
    [Theory]
    [InlineData("No job in progress", null, true)]
    [InlineData("Job ID doesn't match", null, false)]
    [InlineData(null, "{\"state\":\"FIN_OK\"}", false)]
    public async Task AnUnansweredStartIsSettledAsNeverBegunOnlyByNoJobInProgress(string? reason, string? json, bool neverStarted)
    {
        // Arrange - a START_PRINT nobody answered, and a printer that later reports a job id
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
        ConnectTimingOutOnPrint();

        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Idle, so the pass that settles the question does not go on to start the print again.
        _clock.Advance(QueueAdvancer.StartUnconfirmedGrace + TimeSpan.FromMinutes(1));
        await ReportAsync(context, PrinterStatus.Idle, jobId: 744);
        ConnectAnswering(command => command is SendJobInfo ?
                             Answered(reason is null ? PrinterEventType.JobInfo : PrinterEventType.Rejected, reason, json) :
                             Unanswered(CommandSendOutcome.ResponseTimedOut));

        // Act
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        (await context.PrintJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(neverStarted ? 0 : 1,
            neverStarted ? "a print that never began is not history" : "nothing the printer said settles it");
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1,
            "the entry is kept either way");
    }

    /// <summary>
    /// A print closed at the bound closes as <c>Unknown</c> when the printer cannot say how it ended -
    /// unanswered, refused, or describing something other than an ending.
    /// </summary>
    /// <param name="kind">How the printer fails to say.</param>
    [Theory]
    [InlineData("unanswered")]
    [InlineData("refused")]
    [InlineData("still printing")]
    public async Task APrintThePrinterCannotSayTheEndOfIsClosedAsUnknown(string kind)
    {
        // Arrange - stranded with a job id, past the bound
        await using HomespoolDbContext context = await SeedAsync();

        context.PrintJobs.Add(new PrintJob
        {
            PrinterId = PrinterId,
            FileName = "stuck.bgcode",
            QueuedByUserId = 1,
            QueuedByScope = CapabilitySet.Format(CapabilitySet.Everything),
            StartedAt = _clock.GetUtcNow(),
            State = PrintState.Starting,
            FirmwareJobId = 753,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        CommandSendResult answer = kind switch
        {
            "unanswered" => Unanswered(CommandSendOutcome.ResponseTimedOut),
            "refused" => Answered(PrinterEventType.Rejected, "Job ID doesn't match"),
            _ => Answered(PrinterEventType.JobInfo, json: "{\"state\":\"PRINTING\"}"),
        };

        IPrinterConnectionActor actor = ConnectAnswering(_ => answer);
        await ReportAsync(context, PrinterStatus.Attention, jobId: 753);
        _clock.Advance(QueueAdvancer.StartingStaleAfter + TimeSpan.FromMinutes(1));

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert - asked, and closed regardless
        await actor.Received(1).SendCommandAsync(Arg.Any<SendJobInfo>(), Arg.Any<CancellationToken>());

        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        job.EndedAt.Should().NotBeNull("an open row would block this printer for good");
        job.State.Should().Be(PrintState.Unknown, "the printer said nothing about how it ended");
    }

    /// <summary>
    /// A transfer that cannot be running is not waited out as though it were under way: the next pass
    /// offers it again, and nothing is held. After each of these the sender has revoked the offer, so a
    /// printer that did take the command has nothing to fetch.
    /// </summary>
    [Theory]
    [InlineData(CommandSendOutcome.NotConnected)]
    [InlineData(CommandSendOutcome.AlreadyInFlight)]
    [InlineData(CommandSendOutcome.SendTimedOut)]
    public async Task ATransferThatCouldNotBeStartedIsOfferedAgainOnTheNextPass(CommandSendOutcome outcome)
    {
        // Arrange - room on the drive, and the offer itself going unanswered
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        IPrinterConnectionActor actor = ConnectAnswering(command => command is SendInfo ?
                                                             Answered(PrinterEventType.Info) :
                                                             Unanswered(outcome));

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        row.TransferStartedAt.Should().BeNull("a transfer that is not under way must not be waited out");
        row.HoldReason.Should().BeNull("not being answered is not a reason to stop the queue");
        OfferedPaths(actor).Should().HaveCount(2, "the next pass offers it again");

        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    /// <summary>
    /// An offer the printer did not answer in time may be a transfer running - firmware acknowledges a
    /// download late when it is busy - so it is waited on while the offer stands, and offered again
    /// once the offer goes uncollected.
    /// </summary>
    [Fact]
    public async Task AnUnansweredOfferIsWaitedOnWhileItStands()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        IPrinterConnectionActor actor = ConnectAnswering(command => command is SendInfo ?
                                                             Answered(PrinterEventType.Info) :
                                                             Unanswered(CommandSendOutcome.ResponseTimedOut));
        using QueueAdvancer advancer = NewAdvancer();

        // Act - the unanswered offer, a pass while it stands, and a pass once it has gone uncollected
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        context.ChangeTracker.Clear();
        PrintFileOnPrinter waiting = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);
        int offeredWhileStanding = OfferedPaths(actor).Length;

        _clock.Advance(TransferOfferStore.CollectWithin);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        waiting.TransferStartedAt.Should().NotBeNull("no answer is not a transfer that failed to start");
        offeredWhileStanding.Should().Be(1, "while the offer stands the printer may be pulling it");
        OfferedPaths(actor).Should().HaveCount(2, "an offer nobody collected is a command the printer never took");
    }

    /// <summary>
    /// The report firmware sends a few seconds into a transfer names the file - enough to print it - but
    /// is not the transfer finishing, so the file is not yet arrived and the transfer is still running.
    /// </summary>
    [Fact]
    public async Task AStartReportNamesTheFileWithoutArrivingIt()
    {
        // Arrange - a transfer in flight, and the printer's early report of the partial
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Idle);
        PrintFile file = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);
        DateTimeOffset started = _clock.GetUtcNow();

        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            TransferStartedAt = started,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await AddEventAsync(PrinterEventType.FileInfo,
                            $"{{\"size\":{file.Size},\"read_only\":true,\"display_name\":\"{file.Name}\",\"path\":\"/usb/QUEUED~1.BGC\"}}");
        ConnectAccepting();

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.PrinterPath.Should().Be("/usb/QUEUED~1.BGC");
        row.ArrivedAt.Should().BeNull("a partial is not a file on the drive until its transfer finishes");
        row.TransferStartedAt.Should().Be(started, "the transfer is still running");
    }

    /// <summary>
    /// Firmware prints a file while it downloads, and so does the queue: once the printer has named the
    /// file, a ready printer is given it without waiting for the transfer to finish.
    /// </summary>
    [Fact]
    public async Task AFileStillArrivingIsPrintedOnceThePrinterHasNamedIt()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        PrintFile file = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            TransferStartedAt = _clock.GetUtcNow(),
            PrinterPath = "/usb/QUEUED~1.BGC",
            Digest = SeededDigest,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        IPrinterConnectionActor actor = ConnectAccepting();

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintJob job = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);

        job.PrinterPath.Should().Be("/usb/QUEUED~1.BGC", "the name the printer gave the partial is the one the print uses");
        job.State.Should().Be(PrintState.Starting);
        OfferedPaths(actor).Should().BeEmpty("the transfer is already running");
    }

    /// <summary>
    /// The printer saying our transfer finished is what makes the file arrived - matched through the
    /// command that started it, under the name the file was sent under.
    /// </summary>
    [Fact]
    public async Task ATransferIsArrivedWhenThePrinterSaysItFinished()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Idle);
        PrintFile file = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            TransferStartedAt = _clock.GetUtcNow(),
            PrinterPath = "/usb/QUEUED~2.BGC",
            DriveName = OwnersName,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await AddTransferEndAsync(PrinterEventType.TransferFinished, "/usb/" + OwnersName);
        ConnectAccepting();

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.ArrivedAt.Should().NotBeNull("the transfer the queue started has finished");
        row.TransferStartedAt.Should().BeNull("nothing is running any more");
        row.PrinterPath.Should().Be("/usb/QUEUED~2.BGC", "the path came from the printer's report, and stands");
    }

    /// <summary>
    /// A transfer that finishes before any report named it is named by the report that follows the
    /// finish, though the file has already arrived.
    /// </summary>
    /// <remarks>
    /// The report comes a pass later, as it does when it misses a flush: within one batch nothing is
    /// saved until the end, so the row would still read as waiting and cover for a lookup that skips
    /// arrived rows.
    /// </remarks>
    [Fact]
    public async Task AFileThatFinishesBeforeItIsNamedIsNamedByTheReportAfter()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Idle);
        PrintFile file = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            TransferStartedAt = _clock.GetUtcNow(),
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await AddTransferEndAsync(PrinterEventType.TransferFinished, "/usb/queued.bgcode");
        using QueueAdvancer advancer = NewAdvancer();

        // Act - the finish in one pass, the report that names the file in the next
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        await AddEventAsync(PrinterEventType.FileInfo,
                            $"{{\"size\":{file.Size},\"read_only\":false,\"display_name\":\"{file.Name}\",\"path\":\"/usb/QUEUED~1.BGC\"}}");
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.ArrivedAt.Should().NotBeNull();
        row.PrinterPath.Should().Be("/usb/QUEUED~1.BGC", "an arrived file with no name could never be printed");
    }

    /// <summary>
    /// A transfer the printer took and gave up leaves nothing of ours on the drive - firmware removes the
    /// partial - so the path it named is forgotten and, after the first retry's wait, the file is
    /// offered again.
    /// </summary>
    [Fact]
    public async Task AnAbortedTransferIsOfferedAgainAfterAWait()
    {
        // Arrange - a transfer the printer had named, and then its end
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        await AddNamedTransferAsync(context);

        await AddTransferEndAsync(PrinterEventType.TransferAborted, "/usb/queued.bgcode");
        IPrinterConnectionActor actor = ConnectAccepting();
        using QueueAdvancer advancer = NewAdvancer();

        // Act - the pass that reads the abort, and one after the wait
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        context.ChangeTracker.Clear();
        PrintFileOnPrinter ended = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);
        int offeredAtOnce = OfferedPaths(actor).Length;

        await using TelemetryDbContext telemetry = TestTelemetryContext.For(context);
        QueueAction waiting = QueueRules.Decide(await NewSnapshotReader(context, telemetry)
                                                    .ReadAsync(PrinterId, TestContext.Current.CancellationToken));

        _clock.Advance(TransferRetryRules.WaitAfter(1));
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        ended.TransferStartedAt.Should().BeNull("the transfer is over");
        ended.PrinterPath.Should().BeNull("the partial it named has gone, and a print of it would be a file error");
        ended.ArrivedAt.Should().BeNull();
        ended.TransferRefusalCount.Should().Be(1, "an abort is counted like a refusal");
        offeredAtOnce.Should().Be(0, "the next attempt waits, as after a refusal");
        waiting.Reason.Should().Be(QueueWaitReason.TransferAbortRetrying, "the page says the printer gave up, not that it refused");
        OfferedPaths(actor).Should().Equal(["/usb/queued.bgcode"], "the entry is still wanted");
        (await context.PrintJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    /// <summary>
    /// A printer that takes the file and gives it up every time is bounded like one refusing it every
    /// time: the sixth abort in a row holds the queue, with one history row, and nothing is sent again.
    /// Acceptance in between does not restart the count, since every aborted attempt was accepted.
    /// </summary>
    [Fact]
    public async Task ATransferAbortedEveryTimeHoldsTheQueueAtTheBound()
    {
        // Arrange - a printer that accepts every offer, and a file whose every transfer aborts
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        IPrinterConnectionActor actor = ConnectAccepting();
        using QueueAdvancer advancer = NewAdvancer();

        // Act - offer, abort, wait, as many times as the bound allows, then once more
        for (uint attempt = 1; attempt <= TransferRetryRules.HoldAfter; attempt++)
        {
            await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);
            await AddTransferEndAsync(PrinterEventType.TransferAborted, "/usb/queued.bgcode", startCommandId: 7000 + attempt);
            await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

            _clock.Advance(TransferRetryRules.WaitAfter((int)attempt));
        }

        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.HoldReason.Should().Be(PrintHoldReason.TransferAborted);
        row.TransferRefusalCount.Should().Be(TransferRetryRules.HoldAfter);
        OfferedPaths(actor).Should().HaveCount(TransferRetryRules.HoldAfter, "a held queue sends nothing");

        PrintJob recorded = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);
        recorded.State.Should().Be(PrintState.Failed);
        recorded.Reason.Should().Be(TransferRetryRules.TransferAbortedCode);
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1,
            "somebody still wants this printed, and decides what happens next");
    }

    /// <summary>
    /// A finished transfer ends a count of aborts, so a later abort starts again from one.
    /// </summary>
    [Fact]
    public async Task AFinishedTransferEndsTheCountOfAborts()
    {
        // Arrange - a row carrying earlier aborts, whose latest transfer then finishes
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Idle);
        PrintFileOnPrinter named = await AddNamedTransferAsync(context);

        named.TransferRefusalCount = 3;
        named.TransferRefusedAt = _clock.GetUtcNow() - TimeSpan.FromMinutes(5);
        named.TransferRefusalCode = TransferRetryRules.TransferAbortedCode;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await AddTransferEndAsync(PrinterEventType.TransferFinished, "/usb/queued.bgcode");

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.ArrivedAt.Should().NotBeNull();
        row.TransferRefusalCount.Should().BeNull("the file arrived, so nothing is failing any more");
    }

    /// <summary>
    /// An accepted offer does not end a count of aborts, since every aborted attempt was accepted first -
    /// while it still ends a count of refusals.
    /// </summary>
    [Theory]
    [InlineData(TransferRetryRules.TransferAbortedCode, 2)]
    [InlineData("STORAGE_FAILURE", null)]
    public async Task AcceptanceEndsARefusalCountButNotAnAbortCount(string code, int? countAfter)
    {
        // Arrange - a row whose retry wait has run out
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        PrintFile file = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            TransferRefusalCount = 2,
            TransferRefusedAt = _clock.GetUtcNow() - TimeSpan.FromMinutes(5),
            TransferRefusalCode = code,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        ConnectAccepting();

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        (await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken))
            .TransferRefusalCount.Should().Be(countAfter);
    }

    /// <summary>
    /// A transfer stopped at the printer is somebody there saying no, so the queue holds rather than
    /// send it again - with one history row - until a person cancels or re-queues it.
    /// </summary>
    [Fact]
    public async Task ATransferStoppedAtThePrinterHoldsTheQueue()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        await AddNamedTransferAsync(context);

        await AddTransferEndAsync(PrinterEventType.TransferStopped, "/usb/queued.bgcode");
        IPrinterConnectionActor actor = ConnectAccepting();
        using QueueAdvancer advancer = NewAdvancer();

        // Act - the pass that reads the stop, and one long after it
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromHours(1));
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.HoldReason.Should().Be(PrintHoldReason.TransferStopped);
        row.PrinterPath.Should().BeNull("the partial it named has gone");
        row.TransferStartedAt.Should().BeNull();
        OfferedPaths(actor).Should().BeEmpty("sending it again would undo what the person at the printer did");

        PrintJob recorded = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);
        recorded.State.Should().Be(PrintState.Failed);
        recorded.Reason.Should().Contain("stopped at the printer");
    }

    /// <summary>
    /// A transfer with no queue entry behind it - a direct send - ends without a count or a hold: there
    /// is nothing to retry and nothing to stop. The row only stops claiming the partial.
    /// </summary>
    [Theory]
    [InlineData(PrinterEventType.TransferAborted)]
    [InlineData(PrinterEventType.TransferStopped)]
    public async Task AnEndedDirectSendIsNotCountedOrHeld(PrinterEventType ending)
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Idle);
        await AddNamedTransferAsync(context);
        context.QueuedPrints.RemoveRange(context.QueuedPrints);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await AddTransferEndAsync(ending, "/usb/queued.bgcode");

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.PrinterPath.Should().BeNull();
        row.HoldReason.Should().BeNull();
        row.TransferRefusalCount.Should().BeNull();
        (await context.PrintJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    /// <summary>
    /// A path left by an attempt that went stale names nothing the next attempt has reported, so the
    /// file sent again is not printed from it: the queue waits for the printer to name the new transfer.
    /// </summary>
    [Fact]
    public async Task AStaleAttemptsPathIsNotPrintedWhenTheFileIsSentAgain()
    {
        // Arrange - an attempt past the staleness bound, with a path its partial once had
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        PrintFile file = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            TransferStartedAt = _clock.GetUtcNow() - QueueAdvancer.TransferStaleAfter - TimeSpan.FromMinutes(1),
            PrinterPath = "/usb/OLD~1.BGC",
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        IPrinterConnectionActor actor = ConnectAccepting();
        using QueueAdvancer advancer = NewAdvancer();

        // Act - the pass that sends it again, and one after, with the new offer standing
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        OfferedPaths(actor).Should().Equal(["/usb/queued.bgcode"], "the stale attempt is given up on, once");
        row.PrinterPath.Should().BeNull("the old path belongs to a partial this transfer never reported");
        (await context.PrintJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0,
            "printing the old path would print whatever is, or is not, left of the old partial");
    }

    /// <summary>
    /// The end of an earlier attempt, read late, does not end the attempt that replaced it: the command
    /// it points back at was answered before this one began.
    /// </summary>
    [Fact]
    public async Task TheEndOfAnEarlierAttemptDoesNotEndTheOneThatReplacedIt()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Idle);
        PrintFile file = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);
        DateTimeOffset started = _clock.GetUtcNow();

        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            TransferStartedAt = started,
            PrinterPath = "/usb/QUEUED~1.BGC",
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await AddTransferEndAsync(PrinterEventType.TransferAborted, "/usb/queued.bgcode",
                                  answeredAt: started - TimeSpan.FromMinutes(1));

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.TransferStartedAt.Should().Be(started, "this attempt has not ended");
        row.PrinterPath.Should().Be("/usb/QUEUED~1.BGC");
    }

    /// <summary>
    /// A transfer that ends with no <c>start_cmd_id</c> was nothing Homespool commanded - a PrusaLink
    /// upload - and does not end ours, though the printer has one slot and ours is the only one known.
    /// </summary>
    [Fact]
    public async Task AnEndingThatNamesNoCommandIsNotOurs()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Idle);
        PrintFile file = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);
        DateTimeOffset started = _clock.GetUtcNow();

        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            TransferStartedAt = started,
            PrinterPath = "/usb/QUEUED~1.BGC",
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await AddEventAsync(PrinterEventType.TransferFinished, payload: null);

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.ArrivedAt.Should().BeNull("somebody else's upload finishing says nothing about ours");
        row.TransferStartedAt.Should().Be(started);
    }

    /// <summary>
    /// When the question after a <c>FILE_EXISTS</c> goes unanswered, nothing is concluded: the file
    /// keeps its name and the queue does not hold.
    /// </summary>
    [Theory]
    [InlineData(CommandSendOutcome.NotConnected)]
    [InlineData(CommandSendOutcome.AlreadyInFlight)]
    [InlineData(CommandSendOutcome.ResponseTimedOut)]
    [InlineData(CommandSendOutcome.SendTimedOut)]
    public async Task AFileExistsRefusalWhoseFollowUpGoesUnansweredConcludesNothing(CommandSendOutcome outcome)
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        ConnectAnswering(command => command switch
        {
            SendInfo => Answered(PrinterEventType.Info),
            SendFileInfo => Unanswered(outcome),
            _ => new CommandSendResult(CommandSendOutcome.Completed,
                                       new CommandOutcome(PrinterEventType.Rejected, "File already exists")
                                           { MachineReason = "FILE_EXISTS" }),
        });

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.DriveName.Should().Be("queued.bgcode", "nothing said the name belongs to another file");
        row.ArrivedAt.Should().BeNull("nothing said the file on the drive is ours");
        row.HoldReason.Should().BeNull("an unanswered question is not a reason to stop the queue");
        row.TransferStartedAt.Should().BeNull("the next pass tries again");
    }

    /// <summary>
    /// When the free-space question goes unanswered, nothing is sent and nothing is held - "no answer"
    /// is not "no room".
    /// </summary>
    [Theory]
    [InlineData(CommandSendOutcome.NotConnected)]
    [InlineData(CommandSendOutcome.AlreadyInFlight)]
    [InlineData(CommandSendOutcome.ResponseTimedOut)]
    [InlineData(CommandSendOutcome.SendTimedOut)]
    public async Task AnUnansweredFreeSpaceQuestionSendsNothingAndHoldsNothing(CommandSendOutcome outcome)
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        IPrinterConnectionActor actor = ConnectAnswering(command => command is SendInfo ?
                                                             Unanswered(outcome) :
                                                             Answered(PrinterEventType.Finished));

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        OfferedPaths(actor).Should().BeEmpty("whether it fits is not known yet");

        context.ChangeTracker.Clear();
        (await context.PrintFilesOnPrinters.AnyAsync(row => row.HoldReason != null, TestContext.Current.CancellationToken))
            .Should().BeFalse("a busy printer is not a full one");
        (await context.PrintJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0,
            "there is no failure to put in history");
    }

    /// <summary>
    /// A start refused before it reached the printer - another command held the slot - leaves no
    /// question behind, and the entry stays queued.
    /// </summary>
    [Fact]
    public async Task APrintCommandThatNeverLeftLeavesNoQuestion()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
        ConnectAnswering(_ => Unanswered(CommandSendOutcome.AlreadyInFlight));

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        (await context.PrintJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0,
            "the command was never written, so there is nothing to ask the printer about");
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    /// <summary>
    /// Reports that cannot be read, or name nothing waiting, are passed over - and a good report behind
    /// them in the same batch still names the file.
    /// </summary>
    [Fact]
    public async Task UnreadableFileReportsDoNotHideAPathBehindThem()
    {
        // Arrange - a transfer in flight
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Idle);
        PrintFile file = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            TransferStartedAt = _clock.GetUtcNow(),
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        string?[] payloads =
        [
            null,
            "not json",
            "{\"path\":\"/usb/NONAME~1.BGC\"}",
            $"{{\"display_name\":\"{file.Name}\"}}",
            "{\"display_name\":\"stranger.bgcode\",\"path\":\"/usb/STRANG~1.BGC\"}",
            $"{{\"display_name\":\"{file.Name}\",\"path\":\"/usb/QUEUED~1.BGC\"}}",
        ];

        await using (TelemetryDbContext telemetry = TestTelemetryContext.For(_databasePath))
        {
            foreach (string? payload in payloads)
            {
                telemetry.PrinterEvents.Add(new PrinterEvent
                {
                    PrinterId = PrinterId,
                    Timestamp = _clock.GetUtcNow(),
                    EventType = PrinterEventType.FileInfo,
                    Payload = payload,
                });
            }

            await telemetry.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        ConnectAccepting();

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.PrinterPath.Should().Be("/usb/QUEUED~1.BGC", "a report without a path cannot say where the file is");
        row.ArrivedAt.Should().BeNull("naming a file is not the printer saying its transfer finished");
    }

    /// <summary>
    /// A report naming the file but not where it is does not mark it arrived: the path is what a print
    /// has to be started with.
    /// </summary>
    /// <remarks>
    /// A pass of its own, because within one batch a later report still finds the row - nothing is
    /// saved until the batch ends - and would cover for a pathless one accepted before it.
    /// </remarks>
    [Fact]
    public async Task AFileReportWithoutAPathDoesNotMarkTheFileArrived()
    {
        // Arrange - a transfer in flight
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Idle);
        PrintFile file = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            TransferStartedAt = _clock.GetUtcNow(),
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using (TelemetryDbContext telemetry = TestTelemetryContext.For(_databasePath))
        {
            telemetry.PrinterEvents.Add(new PrinterEvent
            {
                PrinterId = PrinterId,
                Timestamp = _clock.GetUtcNow(),
                EventType = PrinterEventType.FileInfo,
                Payload = $"{{\"display_name\":\"{file.Name}\"}}",
            });
            await telemetry.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        ConnectAccepting();

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.PrinterPath.Should().BeNull("a report with no path says nothing a print could be started with");
        row.ArrivedAt.Should().BeNull();
    }

    /// <summary>
    /// One printer's failure does not stop the pass reaching the others.
    /// </summary>
    [Fact]
    public async Task OnePrintersFailureDoesNotStopTheOthers()
    {
        // Arrange - two printers each with a file to send, and the first failing in a way nothing expects
        const int secondPrinterId = 2;

        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        Printer first = await context.Printers.SingleAsync(TestContext.Current.CancellationToken);
        PrintFile file = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        context.Printers.Add(new Printer { Id = secondPrinterId, Uuid = Guid.NewGuid(), TeamId = first.TeamId });
        context.QueuedPrints.Add(new QueuedPrint
        {
            PrinterId = secondPrinterId,
            PrintFileId = file.Id,
            PrintUuid = Guid.NewGuid(),
            Position = 0,
            QueuedByUserId = 1,
            QueuedByScope = CapabilitySet.Format(CapabilitySet.Everything),
            QueuedAt = _clock.GetUtcNow(),
        });
        context.PrinterLiveStates.Add(new PrinterLiveState
        {
            PrinterId = secondPrinterId,
            Status = PrinterStatus.Ready,
            LastSeenAt = _clock.GetUtcNow(),
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        ConnectAnswering(_ => throw new InvalidOperationException("a defect in the first printer's path"));
        IPrinterConnectionActor second = ConnectAnswering(_ => Answered(PrinterEventType.Finished), secondPrinterId);
        FakeLogger<QueueAdvancer> logger = new();

        // Act
        using QueueAdvancer advancer = NewAdvancer(logger);
        await advancer.AdvanceAllAsync(TestContext.Current.CancellationToken);

        // Assert
        OfferedPaths(second).Should().ContainSingle("the second printer's pass ran despite the first");
        logger.Collector.GetSnapshot().Should().Contain(record => record.Level == LogLevel.Error,
                                                        "the failure is still reported");
    }

    /// <summary>
    /// A file still in storage that cannot be opened holds the queue with one line of history, and is
    /// tried again on the recheck clock rather than every tick.
    /// </summary>
    [Fact]
    public async Task AFileThatCannotBeReadHoldsTheQueueAndIsTriedAgainOnTheRecheckClock()
    {
        // Arrange - the file is there, and opening it fails every time
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        IPrinterConnectionActor actor = ConnectAccepting();
        ITransferOffers offers = Substitute.For<ITransferOffers>();
        offers.Offer(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>()).Returns(false);

        using QueueAdvancer advancer = NewAdvancer(offers: offers);

        // Act - the failure, a tick inside the recheck window, and one past it
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        offers.ReceivedCalls().Should().HaveCount(1, "a held file is not retried every tick");

        _clock.Advance(QueueAdvancer.BlockRecheckAfter + TimeSpan.FromSeconds(1));
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        offers.ReceivedCalls().Should().HaveCount(2, "the hold is re-checked by trying the file again");
        OfferedPaths(actor).Should().BeEmpty("nothing could be offered");

        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.HoldReason.Should().Be(PrintHoldReason.FileUnreadable, "the reason has to reach a person, or the queue stalls silently");
        row.TransferStartedAt.Should().BeNull("no transfer is under way");

        PrintJob recorded = await context.PrintJobs.SingleAsync(TestContext.Current.CancellationToken);
        recorded.State.Should().Be(PrintState.Failed);
        recorded.Reason.Should().Contain("queued.bgcode", "history says which file, once, however long the hold lasts");

        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1,
            "somebody still wants this printed");
    }

    /// <summary>
    /// Once the file can be read, the next recheck sends it and the hold lifts with nobody pressing
    /// anything.
    /// </summary>
    [Fact]
    public async Task AnUnreadableFileIsSentOnceItCanBeReadAgain()
    {
        // Arrange - unreadable once, then fixed
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        IPrinterConnectionActor actor = ConnectAccepting();
        ITransferOffers offers = Substitute.For<ITransferOffers>();
        offers.Offer(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>()).Returns(false, true);

        using QueueAdvancer advancer = NewAdvancer(offers: offers);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Act
        _clock.Advance(QueueAdvancer.BlockRecheckAfter + TimeSpan.FromSeconds(1));
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        OfferedPaths(actor).Should().ContainSingle("the file is offered as soon as it opens");

        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.HoldReason.Should().BeNull("the fault was on this side, and it has gone");
        row.BlockedAt.Should().BeNull();
        row.TransferStartedAt.Should().NotBeNull("the transfer is under way");
    }

    /// <summary>
    /// A file deleted between being found and being opened is not held: the next pass finds it
    /// missing and drops the entry, as a delete a moment sooner would have.
    /// </summary>
    [Fact]
    public async Task AFileDeletedWhileBeingSentIsDroppedRatherThanHeld()
    {
        // Arrange - the open fails because the file has just gone
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        ConnectAccepting();
        ITransferOffers offers = Substitute.For<ITransferOffers>();
        offers.Offer(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>())
              .Returns(call =>
              {
                  File.Delete(call.ArgAt<string>(1));

                  return false;
              });

        using QueueAdvancer advancer = NewAdvancer(offers: offers);

        // Act
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        row.HoldReason.Should().BeNull("a file that is gone is not a fault to wait out");
        row.TransferStartedAt.Should().BeNull();

        context.ChangeTracker.Clear();
        (await context.PrintJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0,
            "there is nothing left to print");
    }

    /// <summary>
    /// A printer that answers each command, on either face of the actor, with whatever
    /// <paramref name="answer"/> gives for it. Returned so a test can see what it was asked.
    /// </summary>
    private IPrinterConnectionActor ConnectAnswering(Func<object, CommandSendResult> answer, int printerId = PrinterId)
    {
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.IsOpen.Returns(true);
        actor.SendAsync(Arg.Any<IPrinterIntent>(), Arg.Any<CancellationToken>())
             .Returns(call => Task.FromResult(answer(call.Arg<IPrinterIntent>())));
        actor.SendCommandAsync(Arg.Any<ISendableCommand>(), Arg.Any<CancellationToken>())
             .Returns(call => Task.FromResult(answer(call.Arg<ISendableCommand>())));

        _registry.Register(printerId, actor, overPlaintext: false);

        return actor;
    }

    /// <summary>An answer from the printer, carrying <paramref name="json"/> as its payload when there is one.</summary>
    private static CommandSendResult Answered(PrinterEventType eventType, string? reason = null, string? json = null)
    {
        return new CommandSendResult(CommandSendOutcome.Completed,
                                     new CommandOutcome(eventType, reason),
                                     json is null ? null : JsonSerializer.Deserialize<JsonElement>(json));
    }

    /// <summary>No answer at all, for the reason <paramref name="outcome"/> names.</summary>
    private static CommandSendResult Unanswered(CommandSendOutcome outcome)
    {
        return new CommandSendResult(outcome, null);
    }

    /// <summary>One event from the printer, as the telemetry writer stores it.</summary>
    /// <param name="eventType">What the printer reported.</param>
    /// <param name="payload">The event's <c>data</c> object, as sent, or null for none.</param>
    /// <param name="commandId">The command the event answers, when it answers one.</param>
    /// <param name="at">When it was received; now, by default.</param>
    private async Task AddEventAsync(PrinterEventType eventType,
                                     string? payload,
                                     long? commandId = null,
                                     DateTimeOffset? at = null)
    {
        await using TelemetryDbContext telemetry = TestTelemetryContext.For(_databasePath);

        telemetry.PrinterEvents.Add(new PrinterEvent
        {
            PrinterId = PrinterId,
            Timestamp = at ?? _clock.GetUtcNow(),
            EventType = eventType,
            CommandId = commandId,
            Payload = payload,
        });

        await telemetry.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The seeded file overwritten with other bytes, as an upload's index leaves its row.</summary>
    private async Task OverwriteSeededFileAsync(HomespoolDbContext context)
    {
        PrintFile file = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);
        file.Digest = OverwrittenDigest;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Every command and intent the printer was sent, in order.</summary>
    private static object[] CommandsSent(IPrinterConnectionActor actor)
    {
        return [.. actor.ReceivedCalls()
                        .Select(call => call.GetArguments().FirstOrDefault())
                        .Where(argument => argument is ISendableCommand or IPrinterIntent)
                        .OfType<object>()];
    }

    /// <summary>
    /// The seeded file's row as a direct send leaves it: sent under its own name, the printer took
    /// these bytes, and nothing has said they arrived.
    /// </summary>
    private async Task<PrintFileOnPrinter> AddTakenCopyAsync(HomespoolDbContext context)
    {
        PrintFile file = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        PrintFileOnPrinter row = new()
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            DriveName = file.Name,
            Digest = file.Digest,
        };

        context.PrintFilesOnPrinters.Add(row);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return row;
    }

    /// <summary>
    /// The seeded file's row as a transfer in flight that the printer has named - the state its end
    /// event finds it in.
    /// </summary>
    private async Task<PrintFileOnPrinter> AddNamedTransferAsync(HomespoolDbContext context)
    {
        PrintFile file = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        PrintFileOnPrinter row = new()
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            TransferStartedAt = _clock.GetUtcNow(),
            PrinterPath = "/usb/QUEUED~1.BGC",
        };

        context.PrintFilesOnPrinters.Add(row);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return row;
    }

    /// <summary>
    /// A transfer of ours ending, as firmware reports one: the <c>TRANSFER_INFO</c> answering the
    /// command that started it, and the terminal event pointing back at that command.
    /// </summary>
    /// <param name="ending">Finished, aborted or stopped.</param>
    /// <param name="sentTo">The drive path the command sent the file to.</param>
    /// <param name="answeredAt">When the command was answered; now, by default.</param>
    /// <param name="startCommandId">The id of the command that started it.</param>
    private async Task AddTransferEndAsync(PrinterEventType ending,
                                           string sentTo,
                                           DateTimeOffset? answeredAt = null,
                                           uint startCommandId = 7001)
    {
        await AddEventAsync(PrinterEventType.TransferInfo,
                            $"{{\"path\":\"{sentTo}\",\"start_cmd_id\":{startCommandId},\"type\":\"FROM_CONNECT\"}}",
                            startCommandId, answeredAt);
        await AddEventAsync(ending, $"{{\"start_cmd_id\":{startCommandId}}}");
    }

    // ---- what the printer wrote, in the log ----
    [Fact]
    public async Task AnArrivalIsLoggedWithThePrintersPathCleaned()
    {
        // Arrange - a transfer in flight, and the FILE_INFO that ends it naming a path with an escape in it
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        PrintFile file = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            TransferStartedAt = _clock.GetUtcNow(),
            Digest = SeededDigest,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using (TelemetryDbContext telemetry = TestTelemetryContext.For(_databasePath))
        {
            telemetry.PrinterEvents.Add(new PrinterEvent
            {
                PrinterId = PrinterId,
                Timestamp = _clock.GetUtcNow(),
                EventType = PrinterEventType.FileInfo,
                Payload = $"{{\"display_name\":\"{file.Name}\",\"path\":\"{DirtyPathJson}\"}}",
            });
            await telemetry.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        ConnectAccepting();
        FakeLogger<QueueAdvancer> logger = new();

        // Act
        using QueueAdvancer advancer = NewAdvancer(logger);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert - the arrival line, and the start line that reads the same path back from its row
        ShouldHaveLogged(logger, "PrinterPath", CleanedPath);
        ShouldHaveLogged(logger, "Path", CleanedPath);
        ShouldNotHaveLoggedAnEscape(logger);
    }

    /// <summary>
    /// Two users' files of one name on one printer: the arrival belongs to the transfer in flight, and
    /// matching by name alone threw on every pass, before the watermark moved, so the printer's whole
    /// queue stopped for good.
    /// </summary>
    /// <param name="theirsArrived">
    /// Whether the other user's copy is already on the drive, or is a row left waiting with no transfer
    /// running - the case where only preferring the transfer in flight picks the right one.
    /// </param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFileReportIsMatchedToTheTransferInFlightWhenAnotherUsersFileHasTheName(bool theirsArrived)
    {
        // Arrange - the seeded file is the other user's, and has the lower id, so a lookup that
        // merely took the first row it read would find it; the second user's transfer is in flight.
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        PrintFile theirs = await context.PrintFiles.SingleAsync(TestContext.Current.CancellationToken);

        context.Users.Add(new HSUser("other@example.com")
        {
            Id = 2,
            Email = "other@example.com",
            NormalizedEmail = "OTHER@EXAMPLE.COM",
            NormalizedUserName = "OTHER@EXAMPLE.COM",
        });

        PrintFile ours = new() { UserId = 2, Name = theirs.Name, Size = 2048, UploadedAt = _clock.GetUtcNow() };
        context.PrintFiles.Add(ours);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = PrinterId,
            PrintFileId = theirs.Id,
            ArrivedAt = theirsArrived ? _clock.GetUtcNow() : null,
            PrinterPath = theirsArrived ? "/usb/QUEUED~1.BGC" : null,
        });

        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = PrinterId,
            PrintFileId = ours.Id,
            TransferStartedAt = _clock.GetUtcNow(),
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using (TelemetryDbContext telemetry = TestTelemetryContext.For(_databasePath))
        {
            telemetry.PrinterEvents.Add(new PrinterEvent
            {
                PrinterId = PrinterId,
                Timestamp = _clock.GetUtcNow(),
                EventType = PrinterEventType.FileInfo,
                Payload = $"{{\"display_name\":\"{ours.Name}\",\"path\":\"/usb/QUEUED~2.BGC\"}}",
            });
            await telemetry.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        ConnectAccepting();

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();

        PrintFileOnPrinter named = await context.PrintFilesOnPrinters.SingleAsync(row => row.PrintFileId == ours.Id,
                                                                                  TestContext.Current.CancellationToken);

        named.PrinterPath.Should().Be("/usb/QUEUED~2.BGC", "the transfer in flight is the one this report describes");
        (await context.PrintFilesOnPrinters.SingleAsync(row => row.PrintFileId == theirs.Id, TestContext.Current.CancellationToken))
            .PrinterPath.Should().Be(theirsArrived ? "/usb/QUEUED~1.BGC" : null, "the other user's copy is not the one reported");
    }

    /// <summary>The job a printer describes as its own, which is a path nothing here ever wrote.</summary>
    [Fact]
    public async Task APanelJobIsLoggedWithThePrintersPathCleaned()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Idle);
        await ReportAsync(context, PrinterStatus.Printing, jobId: 738);
        ConnectAnsweringJobInfo(DirtyPathJson);
        FakeLogger<QueueAdvancer> logger = new();

        // Act
        using QueueAdvancer advancer = NewAdvancer(logger);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        ShouldHaveLogged(logger, "TheirPath", CleanedPath);
        ShouldNotHaveLoggedAnEscape(logger);
    }

    /// <summary>The same, asked about a start of ours that went unanswered.</summary>
    [Fact]
    public async Task SomebodyElsesJobIsLoggedWithThePrintersPathCleaned()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
        ConnectTimingOutOnPrint();
        FakeLogger<QueueAdvancer> logger = new();

        using QueueAdvancer advancer = NewAdvancer(logger);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        await ReportAsync(context, PrinterStatus.Printing, jobId: 725);
        ConnectAnsweringJobInfo(DirtyPathJson);

        // Act
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        ShouldHaveLogged(logger, "TheirPath", CleanedPath);
        ShouldNotHaveLoggedAnEscape(logger);
    }

    /// <summary>The name the drive already holds the file under, adopted as the printer spells it.</summary>
    [Fact]
    public async Task AnAdoptedFileIsLoggedWithThePrintersPathCleaned()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        await AddTakenCopyAsync(context);
        ConnectRefusingTransferAsExisting(existingSize: OnDiskLength, existingPath: DirtyPathJson);
        FakeLogger<QueueAdvancer> logger = new();

        // Act
        using QueueAdvancer advancer = NewAdvancer(logger);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        ShouldHaveLogged(logger, "PrinterPath", CleanedPath);
        ShouldNotHaveLoggedAnEscape(logger);
    }

    /// <summary>A refusal nobody has read before is logged so somebody can - which is what makes it a way in.</summary>
    [Fact]
    public async Task AnUnknownRefusalIsLoggedCleaned()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: true, status: PrinterStatus.Ready);
        ConnectRefusing("Not now\u001B[2J");
        FakeLogger<QueueAdvancer> logger = new();

        // Act
        using QueueAdvancer advancer = NewAdvancer(logger);
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        ShouldHaveLogged(logger, "Reason", "Not now\uFFFD[2J");
        ShouldNotHaveLoggedAnEscape(logger);
    }

    private static void ShouldHaveLogged(FakeLogger<QueueAdvancer> logger, string property, string value)
    {
        logger.Collector.GetSnapshot()
              .Should().Contain(record => record.StructuredState != null &&
                                          record.StructuredState.Any(pair => pair.Key == property && pair.Value == value));
    }

    private static void ShouldNotHaveLoggedAnEscape(FakeLogger<QueueAdvancer> logger)
    {
        logger.Collector.GetSnapshot()
              .Where(record => record.StructuredState != null)
              .SelectMany(record => record.StructuredState!)
              .Should().NotContain(pair => pair.Value != null && pair.Value.Contains('\u001B'));
    }

    /// <summary>
    /// A printer that describes the job it is running as <paramref name="path"/>, and still will not
    /// answer a print command. Returned so a test can count how often it was asked.
    /// </summary>
    private IPrinterConnectionActor ConnectAnsweringJobInfo(string path, string? displayName = null)
    {
        string name = displayName is null ? string.Empty : $",\"display_name\":\"{displayName}\"";

        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.IsOpen.Returns(true);
        actor.SendAsync(Arg.Any<IPrinterIntent>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new CommandSendResult(CommandSendOutcome.ResponseTimedOut, null)));
        actor.SendCommandAsync(Arg.Any<ISendableCommand>(), Arg.Any<CancellationToken>())
             .Returns(call => call.Arg<ISendableCommand>() is SendJobInfo ?
                          Task.FromResult(new CommandSendResult(
                                              CommandSendOutcome.Completed,
                                              new CommandOutcome(PrinterEventType.JobInfo, null),
                                              JsonSerializer.Deserialize<JsonElement>(
                                                  $"{{\"state\":\"PRINTING\",\"path\":\"{path}\"{name}}}"))) :
                          Task.FromResult(new CommandSendResult(CommandSendOutcome.ResponseTimedOut, null)));

        _registry.Register(PrinterId, actor, overPlaintext: false);

        return actor;
    }

    /// <summary>An advancer over this test's database, clock, registry and file store.</summary>
    /// <param name="logger">Where the advancer logs, when a test reads it.</param>
    /// <param name="offers">Stands in for the offer store the sender opens files through, when a test needs it to fail.</param>
    private QueueAdvancer NewAdvancer(ILogger<QueueAdvancer>? logger = null, ITransferOffers? offers = null)
    {
        ServiceCollection services = new();
        services.AddDbContext<HomespoolDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
        services.AddDbContext<TelemetryDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
        services.AddScoped<PrinterAccessService>();
        services.AddSingleton(_registry);
        services.AddScoped<PrinterCommandService>();

        // The transfer path resolves these. Rooted in a temp directory: the staleness rule is about a
        // timestamp, and the file merely has to exist for the loop to get that far.
        services.Configure<PrintFileStorageOptions>(options => options.Directory = _storeRoot);
        services.AddSingleton<IHostEnvironmentAccessor>(new HostEnvironmentAccessor(_storeRoot));

        // The fake clock, not the real one: QueueSnapshotReader resolves TimeProvider from here and
        // decides transfer staleness with it, so registering TimeProvider.System would quietly make
        // the staleness cases measure wall-clock time and pass for the wrong reason.
        services.AddSingleton<TimeProvider>(_clock);
        services.AddScoped<QueueSnapshotReader>();
        services.AddSingleton<UserFileStore>();
        services.AddScoped<PrintFileCatalog>();

        // As itself and as the interface, the way Program.cs registers it: the key store follows the
        // concrete store's retirements, so it has to be able to find it.
        services.AddSingleton(new TransferOfferStore(_clock, TestOptions.Monitor(new PrusaConnectOptions()), NullLogger<TransferOfferStore>.Instance));
        services.AddSingleton<ITransferOffers>(sp => offers ?? sp.GetRequiredService<TransferOfferStore>());
        services.AddSingleton<EncryptedTransferOffers>();
        services.AddSingleton(Options.Create(new PrusaConnectOptions()));
        services.AddScoped<PrintFileSender>();
        services.AddScoped<PrinterDriveNames>();
        services.AddScoped<PrinterDriveCopies>();
        services.AddLogging();

        return new QueueAdvancer(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            _registry,
            _signal,
            _clock,
            logger ?? NullLogger<QueueAdvancer>.Instance);
    }

    /// <summary>
    /// A second member's file of <paramref name="name"/> on this printer's drive - arrived by the
    /// queue's record, or only reserved under that name by a direct send.
    /// </summary>
    private async Task AddOtherUsersFileOnThePrinterAsync(HomespoolDbContext context, string name, bool arrived = true)
    {
        context.Users.Add(new HSUser("other@example.com")
        {
            Id = 2,
            Email = "other@example.com",
            NormalizedEmail = "OTHER@EXAMPLE.COM",
            NormalizedUserName = "OTHER@EXAMPLE.COM",
        });

        PrintFile theirs = new() { UserId = 2, Name = name, Size = 2048, UploadedAt = _clock.GetUtcNow() };
        context.PrintFiles.Add(theirs);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = PrinterId,
            PrintFileId = theirs.Id,
            ArrivedAt = arrived ? _clock.GetUtcNow() : null,
            PrinterPath = arrived ? "/usb/QUEUED~1.BGC" : null,
            DriveName = arrived ? null : name,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A user, a team, a printer, a file, and one thing queued on it.</summary>
    private async Task<HomespoolDbContext> SeedAsync(bool arrived = false, PrinterStatus status = PrinterStatus.Idle)
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={_databasePath}")
                                                       .Options;

        HomespoolDbContext context = new(options);
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
            Capabilities = TestMemberships.Literal(CapabilityPresets.Operator),
        });

        context.Printers.Add(new Printer { Id = PrinterId, Uuid = Guid.NewGuid(), TeamId = team.Id });

        PrintFile file = new()
        {
            UserId = 1,
            Name = "queued.bgcode",
            Size = 1024,
            Digest = SeededDigest,
            UploadedAt = _clock.GetUtcNow(),
        };

        context.PrintFiles.Add(file);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.QueuedPrints.Add(new QueuedPrint
        {
            PrinterId = PrinterId,
            PrintFileId = file.Id,
            PrintUuid = QueuedPrintUuid,
            Position = 0,
            QueuedByUserId = 1,
            QueuedByScope = CapabilitySet.Format(CapabilitySet.Everything),
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
                DriveName = file.Name,
                Digest = SeededDigest,
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
