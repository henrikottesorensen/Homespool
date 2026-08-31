using System;
using System.IO;
using System.Text.Json;
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

    /// <summary>The handle the seeded entry is enqueued under - fixed, so assertions can name it.</summary>
    private static readonly Guid QueuedTrackingId = new("11111111-2222-3333-4444-555555555555");

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
        failure.TrackingId.Should().Be(QueuedTrackingId,
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

        _registry.Register(PrinterId, actor);
    }

    /// <summary>A printer that accepts whatever it is sent, so a refusal can only be ours.</summary>
    private void ConnectAccepting()
    {
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.IsOpen.Returns(true);
        actor.SendCommandAsync(Arg.Any<ISendableCommand>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new CommandSendResult(CommandSendOutcome.Completed,
                                                            new CommandOutcome(PrinterEventType.Finished, null))));
        actor.SendAsync(Arg.Any<IPrinterIntent>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new CommandSendResult(CommandSendOutcome.Completed,
                                                            new CommandOutcome(PrinterEventType.Finished, null))));

        _registry.Register(PrinterId, actor);
    }

    /// <summary>
    /// A printer that refuses the transfer with <c>FILE_EXISTS</c> and then answers
    /// <c>SEND_FILE_INFO</c> about whatever is already sitting there.
    /// </summary>
    /// <param name="existingSize">What the drive says the existing file's size is, or null to answer without one.</param>
    /// <param name="existingPath">The 8.3 alias the printer reports, which is what a print must use.</param>
    private void ConnectRefusingTransferAsExisting(long? existingSize, string existingPath = "/usb/SHAPE-~1.BGC")
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
                     string json = existingSize is { } size ?
                         $"{{\"path\":\"{existingPath}\",\"size\":{size}}}" :
                         $"{{\"path\":\"{existingPath}\"}}";

                     return Task.FromResult(new CommandSendResult(CommandSendOutcome.Completed,
                                                                  new CommandOutcome(PrinterEventType.FileInfo, null),
                                                                  JsonSerializer.Deserialize<JsonElement>(json)));
                 }

                 return Task.FromResult(new CommandSendResult(CommandSendOutcome.Completed,
                                                              new CommandOutcome(PrinterEventType.Rejected, "File already exists")
                                                                  { MachineReason = "FILE_EXISTS" }));
             });

        _registry.Register(PrinterId, actor);
    }

    /// <summary>
    /// The bytes are already where we wanted them, so the transfer is not an error to retry - it is
    /// our own bookkeeping being wrong, and the file is adopted under the name the printer uses.
    /// </summary>
    [Fact]
    public async Task AFileAlreadyOnTheDriveAtTheSameSizeIsAdopted()
    {
        // Arrange - we do not think it has arrived; the drive disagrees, at the same size.
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        ConnectRefusingTransferAsExisting(existingSize: OnDiskLength);

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.ArrivedAt.Should().NotBeNull("the file is on the drive, whoever put it there");
        row.PrinterPath.Should().Be("/usb/SHAPE-~1.BGC",
                                    "the alias the printer answered with is what START_PRINT has to use, and it is unguessable from here");
        row.HoldReason.Should().BeNull("nothing is in the way");
    }

    /// <summary>
    /// A name can match while the bytes do not, and printing somebody else's model is worse than
    /// stopping - so the queue holds with a sentence rather than adopting or retrying for ever.
    /// </summary>
    [Fact]
    public async Task AFileAlreadyOnTheDriveAtADifferentSizeHoldsTheQueue()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync(arrived: false, status: PrinterStatus.Ready);
        await WriteFileOnDiskAsync("queued.bgcode");
        ConnectRefusingTransferAsExisting(existingSize: OnDiskLength + 4096);

        // Act
        using QueueAdvancer advancer = NewAdvancer();
        await advancer.AdvanceAsync(PrinterId, TestContext.Current.CancellationToken);

        // Assert
        context.ChangeTracker.Clear();
        PrintFileOnPrinter row = await context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken);

        row.ArrivedAt.Should().BeNull("a matching name is not matching content");
        row.HoldReason.Should().Be(PrintHoldReason.FileExistsDifferentSize,
                                   "the reason has to reach a person, or the queue stalls silently");
        row.HoldPrinterFileBytes.Should().Be(OnDiskLength + 4096,
                                             "the page states both sizes, and this is the one only the printer knows");
        row.BlockedAt.Should().NotBeNull("the hold is re-checked on a clock, not every tick");

        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1,
            "somebody still wants this printed - a block is not a cancellation");
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
        adopted.TrackingId.Should().Be(QueuedTrackingId, "the intention and the print it produced stay connected");

        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0,
            "now - and only now - has the entry done its job");
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
        adopted.TrackingId.Should().Be(QueuedTrackingId, "the intention and the print it produced stay connected");
        adopted.QueuedByUserId.Should().Be(1);
        adopted.PrinterPath.Should().Be("/usb/QUEUED~1.BGC");
        adopted.CommandedAt.Should().BeNull("no command of ours started this print, and null is that record");

        (await context.QueuedPrints.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0,
            "the entry surviving is what used to print the file a second time");
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

    /// <summary>Overwrites what the printer is last known to have said.</summary>
    /// <remarks>
    /// <c>LastSeenAt</c> moves with it, deliberately: a live state that has not been refreshed since
    /// the command is not an answer about it, and several rules turn on exactly that.
    /// </remarks>
    private async Task ReportAsync(HomespoolDbContext context, PrinterStatus status, int? jobId)
    {
        context.ChangeTracker.Clear();

        PrinterLiveState live = await context.PrinterLiveStates
                                             .SingleAsync(state => state.PrinterId == PrinterId,
                                                          TestContext.Current.CancellationToken);

        live.Status = status;
        live.JobId = jobId;
        live.LastSeenAt = _clock.GetUtcNow();

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

        _registry.Register(PrinterId, actor);
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

        _registry.Register(PrinterId, actor);

        return actor;
    }

    /// <summary>
    /// A printer that describes the job it is running as <paramref name="path"/>, and still will not
    /// answer a print command. Returned so a test can count how often it was asked.
    /// </summary>
    private IPrinterConnectionActor ConnectAnsweringJobInfo(string path)
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
                                                  $"{{\"state\":\"PRINTING\",\"path\":\"{path}\"}}"))) :
                          Task.FromResult(new CommandSendResult(CommandSendOutcome.ResponseTimedOut, null)));

        _registry.Register(PrinterId, actor);

        return actor;
    }

    private QueueAdvancer NewAdvancer()
    {
        ServiceCollection services = new();
        services.AddDbContext<HomespoolDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
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
        services.AddSingleton<ITransferOffers>(
            new TransferOfferStore(_clock, NullLogger<TransferOfferStore>.Instance));
        services.AddSingleton<EncryptedTransferOffers>();
        services.AddSingleton(Options.Create(new PrusaConnectOptions()));
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
            Capabilities = TestMemberships.Graded(true, true, false),
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
            TrackingId = QueuedTrackingId,
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
