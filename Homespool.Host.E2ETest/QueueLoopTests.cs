using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.FakePrinter;
using Homespool.Host.Accounts;
using Homespool.Host.Controllers;
using Homespool.Host.Localisation;
using Homespool.Host.PrintFiles;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect;
using Homespool.Host.Queue;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The producer loop, driven against a real connected printer: queue a file, watch it move, make the
/// printer available, watch it print.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only place the loop is demonstrated rather than argued for.</b> Its parts each have
/// unit coverage - <c>QueueRules</c> exhaustively - but nothing else joins queue to transfer to
/// <c>START_PRINT</c> across a real WebSocket, a real command path and a fake that answers from
/// firmware source.
/// </para>
/// <para>
/// The advancer is driven a pass at a time rather than left to its timer. Waiting out poll intervals
/// would make this slow and, worse, flaky in the direction that hides bugs: a test that eventually
/// passes cannot tell "the loop advanced" from "the loop advanced for some other reason".
/// </para>
/// <para>
/// Two waits are unavoidable and are real: telemetry has to reach <c>PrinterLiveState</c> and the
/// printer's <c>FILE_INFO</c> has to reach <c>PrinterEvents</c>, both through
/// <c>TelemetryWriter</c>'s batching. Those are polled on the database, which is the same thing the
/// loop itself reads.
/// </para>
/// </remarks>
public sealed class QueueLoopTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-queueloop-{Guid.NewGuid():N}.db");
    private HomespoolFactory _root = null!;
    private WebApplicationFactory<PrinterAppController> _factory = null!;

    public ValueTask InitializeAsync()
    {
        _root = new HomespoolFactory($"Data Source={_databasePath}");
        _factory = _root.WithWebHostBuilder(_ => { });

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _root.Dispose();

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// A printer that reports itself often. The synthetic source idles at 15 s, which is a fair
    /// imitation of hardware and far too slow here - the loop reads <c>PrinterLiveState</c>, so
    /// nothing it does can be observed faster than the printer says what it is doing.
    /// </summary>
    private static FakePrinterOptions FastTelemetry()
    {
        return new FakePrinterOptions
        {
            TelemetrySource = new SyntheticTelemetrySource
            {
                IdleInterval = TimeSpan.FromMilliseconds(200),
                PrintingInterval = TimeSpan.FromMilliseconds(200),
            },
        };
    }

    /// <summary>
    /// Rebuilds the host with a shorter command response timeout, so a printer that answers late can
    /// answer a second late rather than eleven.
    /// </summary>
    /// <remarks>
    /// <b>Per test rather than for the class</b>, because the timeout is the thing under test in
    /// exactly one of them and a shared short one would make every other test here sensitive to how
    /// busy the machine is. The database is the outer factory's, so the rebuilt host reads the same
    /// file.
    /// </remarks>
    private void UseCommandTimeout(TimeSpan timeout)
    {
        _factory.Dispose();
        _factory = _root.WithWebHostBuilder(
            builder => builder.ConfigureServices(
                services => services.Configure<PrusaConnectOptions>(
                    connect => connect.CommandResponseTimeoutSeconds = timeout.TotalSeconds)));

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await predicate())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private static async Task EndRunAsync(FakePrinterClient fake, Task run)
    {
        await fake.DisposeAsync();

        try
        {
            await run;
        }
        catch (Exception)
        {
            // The run loop ends however the socket ended; the assertions above are what matter.
        }
    }

    /// <summary>
    /// The whole loop: a queued file reaches the drive on its own, and prints once - and only once -
    /// somebody makes the printer available.
    /// </summary>
    [Fact]
    public async Task AQueuedFileTransfersItselfAndPrintsWhenThePrinterIsMadeReady()
    {
        // Arrange - an enrolled, connected printer with one file queued on it
        (PrinterIdentity identity, string token, int printerId, long userId) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        await using FakePrinterClient fake = new(identity, TimeProvider.System, FastTelemetry()) { Token = token };
        await fake.ConnectAsync(ConnectAsync, TestContext.Current.CancellationToken);
        Task run = fake.RunAsync(TestContext.Current.CancellationToken);

        (await WaitUntilAsync(() => Task.FromResult(Registry.IsConnected(printerId)), TimeSpan.FromSeconds(10))).Should().BeTrue();

        await UploadAsync(userId, "benchy.bgcode");
        await EnqueueAsync(printerId, userId, "benchy.bgcode");

        // Act 1 - the loop offers the file without anyone making the printer ready. Pipelining: a
        // transfer is not gated on availability.
        await AdvanceAsync(printerId);

        bool transferred = await WaitUntilAsync(
            () => Task.FromResult(fake.Device.Storage.Find("/usb/benchy.bgcode") is not null),
            TimeSpan.FromSeconds(30));

        transferred.Should().BeTrue("the loop should send a queued file to a connected printer whatever it is doing");

        // The printer's FILE_INFO has to be persisted before the loop can know what to print with.
        (await WaitUntilAsync(async () => await ArrivedAsync(printerId), TimeSpan.FromSeconds(30))).Should().BeTrue();

        // Act 2 - still Idle, so nothing should print however many passes run.
        await AdvanceAsync(printerId);
        await AdvanceAsync(printerId);

        // Assert - the gate with nothing underneath it
        fake.Device.State.Should().Be(DeviceState.Idle,
                                      "Idle means nobody has offered the printer up for work, and firmware would have accepted the print");
        (await QueueDepthAsync(printerId)).Should().Be(1, "the entry is still waiting, not consumed");

        // Act 3 - a person makes it ready, and telemetry carries that to the live state the loop reads
        fake.Device.TrySetReady().Should().BeTrue();

        (await WaitUntilAsync(() => StatusIsAsync(printerId, PrinterStatus.Ready), TimeSpan.FromSeconds(30))).Should().BeTrue();

        await AdvanceAsync(printerId);

        // Assert - it printed, and the queue is empty
        (await WaitUntilAsync(() => Task.FromResult(fake.Device.State == DeviceState.Printing),
                              TimeSpan.FromSeconds(10))).Should().BeTrue();

        (await QueueDepthAsync(printerId)).Should().Be(0, "the entry is consumed once the printer takes the print");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A file queued twice moves once - the transfer belongs to <i>(file, printer)</i> rather than to
    /// the queue entry, which is what stops a queue of five copies sending five times.
    /// </summary>
    [Fact]
    public async Task AFileQueuedTwiceIsOnlyTransferredOnce()
    {
        // Arrange
        (PrinterIdentity identity, string token, int printerId, long userId) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        await using FakePrinterClient fake = new(identity, TimeProvider.System, FastTelemetry()) { Token = token };
        await fake.ConnectAsync(ConnectAsync, TestContext.Current.CancellationToken);
        Task run = fake.RunAsync(TestContext.Current.CancellationToken);

        (await WaitUntilAsync(() => Task.FromResult(Registry.IsConnected(printerId)), TimeSpan.FromSeconds(10))).Should().BeTrue();

        await UploadAsync(userId, "twice.bgcode");
        await EnqueueAsync(printerId, userId, "twice.bgcode");
        await EnqueueAsync(printerId, userId, "twice.bgcode");

        // Act
        await AdvanceAsync(printerId);
        (await WaitUntilAsync(async () => await ArrivedAsync(printerId), TimeSpan.FromSeconds(30))).Should().BeTrue();

        int afterFirst = fake.Device.LastTransfer is null ? 0 : 1;

        // A second pass with the file already arrived must not offer it again.
        await AdvanceAsync(printerId);
        await AdvanceAsync(printerId);

        // Assert
        afterFirst.Should().Be(1);
        (await ReplicaCountAsync(printerId)).Should().Be(1, "one row per (file, printer), however deep the queue");
        (await QueueDepthAsync(printerId)).Should().Be(2, "nothing printed - the printer was never made ready");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// The history row: opened when the printer takes the print, promoted to <c>Printing</c> once
    /// telemetry says so, and closed <c>Finished</c> when the print ends.
    /// </summary>
    /// <remarks>
    /// The two phases exist because a real printer keeps reporting <c>READY</c> for a few seconds
    /// after accepting <c>START_PRINT</c> - 3.1 s on a Core One - so closing on "no longer printing"
    /// without them would close every print moments after starting it. <b>This test cannot show that
    /// gap</b>: the fake transitions instantly. What it does show is that the phases are traversed in
    /// order and that the row ends with the right outcome.
    /// </remarks>
    [Fact]
    public async Task APrintIsRecordedFromStartingThroughToFinished()
    {
        // Arrange
        (PrinterIdentity identity, string token, int printerId, long userId) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        await using FakePrinterClient fake = new(identity, TimeProvider.System, FastTelemetry()) { Token = token };
        await fake.ConnectAsync(ConnectAsync, TestContext.Current.CancellationToken);
        Task run = fake.RunAsync(TestContext.Current.CancellationToken);

        (await WaitUntilAsync(() => Task.FromResult(Registry.IsConnected(printerId)), TimeSpan.FromSeconds(10)))
            .Should().BeTrue();

        await UploadAsync(userId, "history.bgcode");
        Guid handle = await EnqueueAsync(printerId, userId, "history.bgcode");

        await AdvanceAsync(printerId);
        (await WaitUntilAsync(async () => await ArrivedAsync(printerId), TimeSpan.FromSeconds(30))).Should().BeTrue();

        fake.Device.TrySetReady().Should().BeTrue();
        (await WaitUntilAsync(() => StatusIsAsync(printerId, PrinterStatus.Ready), TimeSpan.FromSeconds(30)))
            .Should().BeTrue();

        // Act - the print starts
        await AdvanceAsync(printerId);

        // Assert - a row exists, and it reaches Printing once telemetry reports it
        (await WaitUntilAsync(async () => await OutcomeIsAsync(printerId, PrintState.Printing),
                              TimeSpan.FromSeconds(30))).Should().BeTrue("the row is promoted once the printer reports PRINTING");

        PrintJob printing = await ActiveAsync(printerId);
        printing.FileName.Should().Be("history.bgcode");
        printing.TrackingId.Should().Be(handle,
                                        "the handle the enqueue returned is the one identifier that survives the start of the print");
        printing.QueuedByUserId.Should().Be(userId);
        printing.PrinterPath.Should().StartWith("/usb/");
        printing.FirmwareJobId.Should().NotBeNull("telemetry carries job_id for the whole print");
        printing.EndedAt.Should().BeNull("an open row is the active print");

        // Act - the print ends the way a print ends
        fake.Device.FinishPrint().Should().BeTrue();

        (await WaitUntilAsync(() => StatusIsAsync(printerId, PrinterStatus.Finished), TimeSpan.FromSeconds(30)))
            .Should().BeTrue();

        await AdvanceAsync(printerId);

        // Assert - closed, and no longer the active print
        PrintJob finished = await SingleJobAsync(printerId);
        finished.State.Should().Be(PrintState.Finished);
        finished.EndedAt.Should().NotBeNull();

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A printer that takes the print and answers too late is not treated as having refused it - and
    /// the file is not printed a second time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect of 2026-08-21, reproduced end to end.</b> A queued file transferred, the printer
    /// was made ready, <c>START_PRINT</c> went out - and the printer began homing and heating and did
    /// not answer inside the command timeout. The loop recorded that as "the print did not happen",
    /// so the entry stayed in the queue with a live print running, ready to print the same file again
    /// the moment somebody cleared the bed. That is the last act of this test.
    /// </para>
    /// <para>
    /// <b>The delay is scoped to <c>START_PRINT</c> because the printer's slowness was.</b> Hardware
    /// defers the ack of the command that set it working; the questions asked before and after are
    /// answered at ordinary speed, and the question asked <i>because</i> that command went unanswered
    /// is the whole resolution. A fake that answered everything late could not reach the interesting
    /// case at all.
    /// </para>
    /// <para>
    /// <b>What this cannot show</b>, for the standing reason: the fake transitions instantly, so the
    /// printer here is <c>PRINTING</c> the moment it accepts. Real firmware spends seconds still
    /// reporting <c>READY</c>, which is why the resolution rules have a grace period the fake never
    /// exercises - that half lives in <c>PrintStartRulesTests</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APrintTheHardwareTookButAnsweredLateForIsNotStartedTwice()
    {
        // Arrange - a command timeout a test can outlast, and a printer that acknowledges START_PRINT
        // well after it
        UseCommandTimeout(TimeSpan.FromSeconds(1));

        (PrinterIdentity identity, string token, int printerId, long userId) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        FakePrinterOptions options = FastTelemetry();
        options = new FakePrinterOptions
        {
            TelemetrySource = options.TelemetrySource,
            Policy = new DelayedReplyPolicy(new FirmwareFaithfulPolicy(identity, TimeProvider.System),
                                            TimeSpan.FromSeconds(4),
                                            new HashSet<string>(StringComparer.Ordinal) { "START_PRINT" }),
        };

        await using FakePrinterClient fake = new(identity, TimeProvider.System, options) { Token = token };
        await fake.ConnectAsync(ConnectAsync, TestContext.Current.CancellationToken);
        Task run = fake.RunAsync(TestContext.Current.CancellationToken);

        (await WaitUntilAsync(() => Task.FromResult(Registry.IsConnected(printerId)), TimeSpan.FromSeconds(10)))
            .Should().BeTrue();

        await UploadAsync(userId, "late.bgcode");
        await EnqueueAsync(printerId, userId, "late.bgcode");

        await AdvanceAsync(printerId);
        (await WaitUntilAsync(async () => await ArrivedAsync(printerId), TimeSpan.FromSeconds(30))).Should().BeTrue();

        fake.Device.TrySetReady().Should().BeTrue();
        (await WaitUntilAsync(() => StatusIsAsync(printerId, PrinterStatus.Ready), TimeSpan.FromSeconds(30)))
            .Should().BeTrue();

        // Act 1 - the print is commanded, and the answer never arrives in time
        await AdvanceAsync(printerId);

        // Assert - the printer really is printing, and the loop knows only that it asked
        fake.Device.State.Should().Be(DeviceState.Printing, "the printer accepted the command it was slow to answer");

        PrintJob unconfirmed = await ActiveAsync(printerId);
        unconfirmed.State.Should().Be(PrintState.Unconfirmed);
        (await QueueDepthAsync(printerId)).Should().Be(1, "nothing has confirmed that the entry can be consumed");

        // Act 2 - the loop asks the printer what it is printing
        (await WaitUntilAsync(() => StatusIsAsync(printerId, PrinterStatus.Printing), TimeSpan.FromSeconds(30)))
            .Should().BeTrue();

        (await WaitUntilAsync(async () =>
                              {
                                  await AdvanceAsync(printerId);

                                  return (await ActiveAsync(printerId)).State != PrintState.Unconfirmed;
                              },
                              TimeSpan.FromSeconds(30)))
            .Should().BeTrue("SEND_JOB_INFO names the file, which is what identifies the print as ours");

        PrintJob adopted = await ActiveAsync(printerId);
        adopted.FileName.Should().Be("late.bgcode");
        adopted.FirmwareJobId.Should().Be(fake.Device.JobId, "the two id spaces are mapped, not reconciled");
        (await QueueDepthAsync(printerId)).Should().Be(0, "now the entry has done its job");

        // Act 3 - the print ends and somebody clears the bed, which is where the duplicate used to run
        fake.Device.FinishPrint().Should().BeTrue();
        (await WaitUntilAsync(() => StatusIsAsync(printerId, PrinterStatus.Finished), TimeSpan.FromSeconds(30)))
            .Should().BeTrue();

        await AdvanceAsync(printerId);

        fake.Device.TrySetIdle().Should().BeTrue();
        fake.Device.TrySetReady().Should().BeTrue();
        (await WaitUntilAsync(() => StatusIsAsync(printerId, PrinterStatus.Ready), TimeSpan.FromSeconds(30)))
            .Should().BeTrue();

        await AdvanceAsync(printerId);
        await AdvanceAsync(printerId);

        // Assert - one print, and the printer is idle-handed
        fake.Device.State.Should().Be(DeviceState.Ready, "the queue is empty; there is nothing left to print");
        (await JobCountAsync(printerId)).Should().Be(1, "one intention, one print");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A <c>START_PRINT</c> the printer takes but answers <c>REJECTED "No job in progress"</c> is
    /// not read as a refusal: the print is adopted, recorded once, and not started a second time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The phantom of 2026-08-27, reproduced end to end.</b> Firmware renders the ack to a
    /// <c>START_PRINT</c> against its momentary state, and a print it has accepted passes through a
    /// state with no job before it reports <c>PRINTING</c> - so a successful start can be answered
    /// as a rejection, command id and all. The loop used to route that through the transient arm,
    /// which removed the row and kept the entry: the print ran with no record, and the file printed
    /// again later. The fake's knob reproduces exactly that answer while really starting the print,
    /// which is what hardware does.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APrintFalselyRejectedAsNoJobInProgressIsAdoptedNotFailed()
    {
        // Arrange - a printer whose next accepted START_PRINT answers with the false rejection
        (PrinterIdentity identity, string token, int printerId, long userId) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        FirmwareFaithfulPolicy policy = new(identity, TimeProvider.System);
        FakePrinterOptions options = new()
        {
            TelemetrySource = FastTelemetry().TelemetrySource,
            Policy = policy,
        };

        await using FakePrinterClient fake = new(identity, TimeProvider.System, options) { Token = token };
        await fake.ConnectAsync(ConnectAsync, TestContext.Current.CancellationToken);
        Task run = fake.RunAsync(TestContext.Current.CancellationToken);

        (await WaitUntilAsync(() => Task.FromResult(Registry.IsConnected(printerId)), TimeSpan.FromSeconds(10)))
            .Should().BeTrue();

        await UploadAsync(userId, "phantom.bgcode");
        await EnqueueAsync(printerId, userId, "phantom.bgcode");

        await AdvanceAsync(printerId);
        (await WaitUntilAsync(async () => await ArrivedAsync(printerId), TimeSpan.FromSeconds(30))).Should().BeTrue();

        fake.Device.TrySetReady().Should().BeTrue();
        (await WaitUntilAsync(() => StatusIsAsync(printerId, PrinterStatus.Ready), TimeSpan.FromSeconds(30)))
            .Should().BeTrue();

        // Act 1 - the print is commanded, taken, and answered with the lie
        policy.NextStartPrintAnswersNoJobInProgress = true;
        await AdvanceAsync(printerId);

        // Assert - the printer is printing, and the loop holds a question rather than a verdict
        fake.Device.State.Should().Be(DeviceState.Printing, "the rejection lied; the command was accepted");

        PrintJob question = await ActiveAsync(printerId);
        question.State.Should().Be(PrintState.Unconfirmed,
                                   "\"No job in progress\" moments after a command is the start window, not an answer");
        (await QueueDepthAsync(printerId)).Should().Be(1, "nothing has confirmed that the entry can be consumed");

        // Act 2 - telemetry names the job, and the loop asks the printer whose it is
        (await WaitUntilAsync(() => StatusIsAsync(printerId, PrinterStatus.Printing), TimeSpan.FromSeconds(30)))
            .Should().BeTrue();

        (await WaitUntilAsync(async () =>
                              {
                                  await AdvanceAsync(printerId);

                                  return (await ActiveAsync(printerId)).State != PrintState.Unconfirmed;
                              },
                              TimeSpan.FromSeconds(30)))
            .Should().BeTrue("SEND_JOB_INFO names the file, which identifies the print as ours");

        PrintJob adopted = await ActiveAsync(printerId);
        adopted.FileName.Should().Be("phantom.bgcode");
        adopted.FirmwareJobId.Should().Be(fake.Device.JobId);
        adopted.CommandedAt.Should().NotBeNull("this print was commanded; only the ack lied");
        (await QueueDepthAsync(printerId)).Should().Be(0, "the entry has done its job");

        // Act 3 - the print ends and the printer is readied again, where the duplicate used to run
        fake.Device.FinishPrint().Should().BeTrue();
        (await WaitUntilAsync(() => StatusIsAsync(printerId, PrinterStatus.Finished), TimeSpan.FromSeconds(30)))
            .Should().BeTrue();

        await AdvanceAsync(printerId);

        fake.Device.TrySetIdle().Should().BeTrue();
        fake.Device.TrySetReady().Should().BeTrue();
        (await WaitUntilAsync(() => StatusIsAsync(printerId, PrinterStatus.Ready), TimeSpan.FromSeconds(30)))
            .Should().BeTrue();

        await AdvanceAsync(printerId);
        await AdvanceAsync(printerId);

        // Assert - one print, once
        fake.Device.State.Should().Be(DeviceState.Ready, "the queue is empty; there is nothing left to print");
        (await JobCountAsync(printerId)).Should().Be(1, "one intention, one print");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A print started at the printer, of a staged file, is adopted without any command having been
    /// sent - and its entry is consumed, so the file does not print a second time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The other phantom of 2026-08-27, reproduced end to end.</b> A staged file is also the
    /// panel's offer - firmware opens its one-click preview for a file that arrives over the wire -
    /// so the person at the machine can start the queued file before the loop does, and always wins,
    /// being behind a button rather than a poll. No command is ever sent here: the printer simply
    /// begins printing the file the loop staged, exactly as the panel does it.
    /// </para>
    /// <para>
    /// The adopted row records the distinction: <c>CommandedAt</c> is null, which is the start-side
    /// sibling of <c>StoppedByUserId</c>'s null meaning "the panel".
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APanelPrintOfAStagedFileIsAdoptedAndItsEntryConsumed()
    {
        // Arrange - a staged file on an idle printer nobody made ready
        (PrinterIdentity identity, string token, int printerId, long userId) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        await using FakePrinterClient fake = new(identity, TimeProvider.System, FastTelemetry()) { Token = token };
        await fake.ConnectAsync(ConnectAsync, TestContext.Current.CancellationToken);
        Task run = fake.RunAsync(TestContext.Current.CancellationToken);

        (await WaitUntilAsync(() => Task.FromResult(Registry.IsConnected(printerId)), TimeSpan.FromSeconds(10)))
            .Should().BeTrue();

        await UploadAsync(userId, "puck.bgcode");
        Guid handle = await EnqueueAsync(printerId, userId, "puck.bgcode");

        await AdvanceAsync(printerId);
        (await WaitUntilAsync(async () => await ArrivedAsync(printerId), TimeSpan.FromSeconds(30))).Should().BeTrue();

        // Act - the person at the machine starts the staged file; nothing here commands anything
        fake.Device.TryStartPrint("/usb/puck.bgcode").Should().NotBeNull();

        (await WaitUntilAsync(() => StatusIsAsync(printerId, PrinterStatus.Printing), TimeSpan.FromSeconds(30)))
            .Should().BeTrue();

        (await WaitUntilAsync(async () =>
                              {
                                  await AdvanceAsync(printerId);

                                  using IServiceScope scope = _factory.Services.CreateScope();

                                  return await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                                                    .PrintJobs.AnyAsync(job => job.PrinterId == printerId,
                                                                        TestContext.Current.CancellationToken);
                              },
                              TimeSpan.FromSeconds(30)))
            .Should().BeTrue("a running job at the path the loop wrote, for a still-queued file, is ours by construction");

        // Assert - adopted, attributed, and the entry consumed
        PrintJob adopted = await ActiveAsync(printerId);
        adopted.FileName.Should().Be("puck.bgcode");
        adopted.State.Should().Be(PrintState.Printing);
        adopted.TrackingId.Should().Be(handle);
        adopted.QueuedByUserId.Should().Be(userId);
        adopted.FirmwareJobId.Should().Be(fake.Device.JobId);
        adopted.CommandedAt.Should().BeNull("no command of ours started this print, and null is that record");

        (await QueueDepthAsync(printerId)).Should().Be(0,
            "the surviving entry is what used to print the file a second time");

        // Act - the print ends; the row closes like any other
        fake.Device.FinishPrint().Should().BeTrue();
        (await WaitUntilAsync(() => StatusIsAsync(printerId, PrinterStatus.Finished), TimeSpan.FromSeconds(30)))
            .Should().BeTrue();

        await AdvanceAsync(printerId);

        PrintJob finished = await SingleJobAsync(printerId);
        finished.State.Should().Be(PrintState.Finished);
        finished.EndedAt.Should().NotBeNull();

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A file that does not fit holds the queue rather than being skipped or cancelled - spooler
    /// behaviour - and the hold clears by itself once there is room.
    /// </summary>
    /// <remarks>
    /// The failed attempt is written to print history once, carrying both numbers, so a held queue has
    /// something to read rather than merely having stopped. The entry itself stays put: somebody still
    /// wants this printed, and the condition is one a person fixes by deleting files.
    /// </remarks>
    [Fact]
    public async Task AFileThatDoesNotFitHoldsTheQueueUntilThereIsRoom()
    {
        // Arrange - a printer with almost no space left
        (PrinterIdentity identity, string token, int printerId, long userId) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        await using FakePrinterClient fake = new(identity, TimeProvider.System, FastTelemetry()) { Token = token };
        fake.Device.FreeSpace = 4;
        await fake.ConnectAsync(ConnectAsync, TestContext.Current.CancellationToken);
        Task run = fake.RunAsync(TestContext.Current.CancellationToken);

        (await WaitUntilAsync(() => Task.FromResult(Registry.IsConnected(printerId)), TimeSpan.FromSeconds(10)))
            .Should().BeTrue();

        await UploadAsync(userId, "toobig.bgcode");
        await EnqueueAsync(printerId, userId, "toobig.bgcode");

        // Act - the loop asks, is told there is no room, and holds.
        //
        // Polled rather than asserted after one call: a pass skips when the hosted timer already holds
        // this printer's gate, which is deliberate - the running pass reads the same state. So an
        // explicit AdvanceAsync is a request for a pass, not a guarantee of one.
        (await WaitUntilAsync(async () =>
        {
            await AdvanceAsync(printerId);

            return await JobCountAsync(printerId) > 0;
        }, TimeSpan.FromSeconds(30))).Should().BeTrue("the block is recorded once the pass runs");

        // Assert - nothing transferred, the entry is still queued, and history says why
        fake.Device.Storage.Find("/usb/toobig.bgcode").Should().BeNull("a file that does not fit is not sent");
        (await QueueDepthAsync(printerId)).Should().Be(1, "the queue holds rather than dropping the entry");

        PrintJob failure = await SingleJobAsync(printerId);
        failure.State.Should().Be(PrintState.Failed);
        failure.Reason.Should().Contain("Not enough space");
        failure.EndedAt.Should().NotBeNull("nothing printed, so it is opened and closed together");

        // Several more passes must not write more rows - a held queue is not a log.
        await AdvanceAsync(printerId);
        await AdvanceAsync(printerId);
        (await JobCountAsync(printerId)).Should().Be(1, "the failure is recorded on the transition, not per tick");

        // Assert - and the hold is *visible*, which is the point. A held queue whose reason only
        // reaches a log is the silent stall the design rejected twice.
        (await HoldReasonAsync(printerId, userId)).Should().Contain("Not enough space");

        // And the loop's own decision agrees with the banner rather than reporting a transfer that
        // cannot happen - the contradiction that putting the block in the snapshot removed.
        QueueAction blocked = await DecideAsync(printerId);
        blocked.Kind.Should().Be(QueueActionKind.Wait);
        blocked.Reason.Should().Be(QueueWaitReason.InsufficientSpace);

        // Act - somebody frees space. The block is re-checked on its own timer, so this drives the
        // recheck directly rather than waiting a minute for it.
        fake.Device.FreeSpace = 64L * 1024 * 1024;
        await ClearBlockClockAsync(printerId);
        await AdvanceAsync(printerId);

        // Assert - it resumes without anyone pressing anything
        (await WaitUntilAsync(
            () => Task.FromResult(fake.Device.Storage.Find("/usb/toobig.bgcode") is not null),
            TimeSpan.FromSeconds(30))).Should().BeTrue("the hold clears itself once there is room");

        await EndRunAsync(fake, run);
    }

    private Printing.PrinterConnectionRegistry Registry =>
        _factory.Services.GetRequiredService<Printing.PrinterConnectionRegistry>();

    /// <summary>The fake's socket, over the test server's printer listener - where /p/ws lives.</summary>
    /// <summary>
    /// A stop made through Homespool is recorded as ours, with the account that asked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Driven over HTTP rather than through the service</b>, because the endpoint is where the
    /// defect was: <c>PrintJob.StoppedByUserId</c> had no writer while this route could already send
    /// <c>STOP_PRINT</c>, so every stop made here was recorded as one made at the panel. Calling
    /// <c>PrintStopService</c> directly would prove the writing rule and miss the wiring, which was
    /// the whole bug.
    /// </para>
    /// <para>
    /// The stop and the close are deliberately separate steps here, as they are on hardware:
    /// <c>STOP_PRINT</c> is answered the moment the abort is accepted, and the row is closed later
    /// from telemetry - so the attribution has to survive the gap rather than be written into the
    /// close.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AStopMadeHereIsRecordedAgainstWhoeverAskedForIt()
    {
        // Arrange - a printer part-way through a print
        (PrinterIdentity identity, string token, int printerId, long userId) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        await using FakePrinterClient fake = new(identity, TimeProvider.System, FastTelemetry()) { Token = token };
        await fake.ConnectAsync(ConnectAsync, TestContext.Current.CancellationToken);
        Task run = fake.RunAsync(TestContext.Current.CancellationToken);

        (await WaitUntilAsync(() => Task.FromResult(Registry.IsConnected(printerId)), TimeSpan.FromSeconds(10)))
            .Should().BeTrue();

        await UploadAsync(userId, "abandoned.bgcode");
        await EnqueueAsync(printerId, userId, "abandoned.bgcode");

        await AdvanceAsync(printerId);
        (await WaitUntilAsync(async () => await ArrivedAsync(printerId), TimeSpan.FromSeconds(30))).Should().BeTrue();

        fake.Device.TrySetReady().Should().BeTrue();
        (await WaitUntilAsync(() => StatusIsAsync(printerId, PrinterStatus.Ready), TimeSpan.FromSeconds(30)))
            .Should().BeTrue();

        await AdvanceAsync(printerId);
        (await WaitUntilAsync(async () => await OutcomeIsAsync(printerId, PrintState.Printing),
                              TimeSpan.FromSeconds(30))).Should().BeTrue();

        // Act - stop it the way a person would, over the API
        using HttpClient client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                                                                                   await IssueTokenAsync(userId));

        using HttpResponseMessage response = await client.PutAsync(
            $"/api/v1/printers/{await UuidAsync(printerId)}/command/stop", content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Assert - the printer really stopped, the row closed as Stopped, and it names who asked
        (await WaitUntilAsync(() => StatusIsAsync(printerId, PrinterStatus.Stopped), TimeSpan.FromSeconds(30)))
            .Should().BeTrue();

        await AdvanceAsync(printerId);

        PrintJob stopped = await SingleJobAsync(printerId);
        stopped.State.Should().Be(PrintState.Stopped);
        stopped.EndedAt.Should().NotBeNull();
        stopped.StoppedByUserId.Should().Be(userId,
                                            "a stop made here and one made at the panel are the same state change, so this is the only record of which it was");

        await EndRunAsync(fake, run);
    }

    private async Task<string> IssueTokenAsync(long userId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        (_, string plaintext) = await scope.ServiceProvider.GetRequiredService<ApiTokenService>()
                                           .CreateAsync(userId, "e2e", CapabilitySet.Everything, TestContext.Current.CancellationToken);

        return plaintext;
    }

    private async Task<Guid> UuidAsync(int printerId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                          .Printers.Where(printer => printer.Id == printerId)
                          .Select(printer => printer.Uuid)
                          .SingleAsync(TestContext.Current.CancellationToken);
    }

    private async Task<WebSocket> ConnectAsync(FakePrinterConnectRequest request,
                                               CancellationToken cancellationToken)
    {
        WebSocketClient client = _factory.Server.CreateWebSocketClient();
        client.SubProtocols.Add(request.SubProtocol);
        client.ConfigureRequest = httpRequest =>
        {
            foreach (System.Collections.Generic.KeyValuePair<string, string> header in request.Headers)
            {
                httpRequest.Headers[header.Key] = header.Value;
            }
        };

        return await client.ConnectAsync(PrinterListener.WebSocketUri(_factory), cancellationToken);
    }

    /// <summary>Drives exactly one pass, rather than waiting out the advancer's poll interval.</summary>
    private async Task AdvanceAsync(int printerId)
    {
        await _factory.Services.GetRequiredService<QueueAdvancer>()
                      .AdvanceAsync(printerId, TestContext.Current.CancellationToken);
    }

    private async Task UploadAsync(long userId, string name)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        PrintFileCatalog catalog = scope.ServiceProvider.GetRequiredService<PrintFileCatalog>();

        await catalog.SaveAsync(Caller.Unscoped(userId), name, new MemoryStream(Encoding.UTF8.GetBytes("G28 ; home\nG1 X10\n")),
                                overwrite: false, TestContext.Current.CancellationToken);
    }

    private async Task<Guid> EnqueueAsync(int printerId, long userId, string name)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        EnqueueOutcome outcome = await scope.ServiceProvider.GetRequiredService<PrintQueueService>()
                                             .EnqueueAsync(printerId, Caller.Unscoped(userId), name,
                                                           TestContext.Current.CancellationToken);

        return outcome.Queued.TrackingId;
    }

    private async Task<bool> OutcomeIsAsync(int printerId, PrintState outcome)
    {
        await AdvanceAsync(printerId);

        using IServiceScope scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                          .PrintJobs.AnyAsync(job => job.PrinterId == printerId && job.State == outcome,
                                              TestContext.Current.CancellationToken);
    }

    private async Task<PrintJob> ActiveAsync(int printerId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                          .PrintJobs.SingleAsync(job => job.PrinterId == printerId && job.EndedAt == null,
                                                 TestContext.Current.CancellationToken);
    }

    private async Task<PrintJob> SingleJobAsync(int printerId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                          .PrintJobs.SingleAsync(job => job.PrinterId == printerId,
                                                 TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// What a person looking at the printer would be told about the hold, in English.
    /// </summary>
    /// <remarks>
    /// Resolved here rather than asserted as a key, because what these tests are about is whether a
    /// held queue explains itself - and a key proves the row was written, not that it says anything.
    /// </remarks>
    private async Task<string?> HoldReasonAsync(int printerId, long userId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        IServiceProvider services = scope.ServiceProvider;

        MessageKey? hold = await services.GetRequiredService<PrintHistoryService>()
                                         .GetHoldReasonAsync(printerId, Caller.Unscoped(userId),
                                                             TestContext.Current.CancellationToken);

        return hold is null ? null : services.GetRequiredService<ErrorText>().For(hold);
    }

    /// <summary>What the loop would do right now - the same question the page and the API ask.</summary>
    private async Task<QueueAction> DecideAsync(int printerId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        QueueSnapshot snapshot = await scope.ServiceProvider.GetRequiredService<QueueSnapshotReader>()
                                            .ReadAsync(printerId, TestContext.Current.CancellationToken);

        return QueueRules.Decide(snapshot);
    }

    private async Task<int> JobCountAsync(int printerId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                          .PrintJobs.CountAsync(job => job.PrinterId == printerId,
                                                TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Ages the block so the next pass re-checks, instead of the test waiting out
    /// <see cref="QueueAdvancer.BlockRecheckAfter"/>.
    /// </summary>
    private async Task ClearBlockClockAsync(int printerId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        foreach (PrintFileOnPrinter row in await context.PrintFilesOnPrinters
                                                        .Where(candidate => candidate.PrinterId == printerId)
                                                        .ToListAsync(TestContext.Current.CancellationToken))
        {
            row.BlockedAt = DateTimeOffset.UnixEpoch;
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> QueueDepthAsync(int printerId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                          .QueuedPrints.CountAsync(queued => queued.PrinterId == printerId,
                                                   TestContext.Current.CancellationToken);
    }

    private async Task<int> ReplicaCountAsync(int printerId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                          .PrintFilesOnPrinters.CountAsync(row => row.PrinterId == printerId,
                                                           TestContext.Current.CancellationToken);
    }

    /// <summary>Whether the loop has seen the printer's own <c>FILE_INFO</c> and recorded its path.</summary>
    private async Task<bool> ArrivedAsync(int printerId)
    {
        // A pass is what turns persisted events into arrival, so this drives one before looking.
        await AdvanceAsync(printerId);

        using IServiceScope scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                          .PrintFilesOnPrinters.AnyAsync(
                              row => row.PrinterId == printerId && row.PrinterPath != null,
                              TestContext.Current.CancellationToken);
    }

    private async Task<bool> StatusIsAsync(int printerId, PrinterStatus status)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                          .PrinterLiveStates.AnyAsync(
                              state => state.PrinterId == printerId && state.Status == status,
                              TestContext.Current.CancellationToken);
    }
}
