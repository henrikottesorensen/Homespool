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
    /// A printer that describes the job it is running as <paramref name="path"/>, and still will not
    /// answer a print command.
    /// </summary>
    private void ConnectAnsweringJobInfo(string path)
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
