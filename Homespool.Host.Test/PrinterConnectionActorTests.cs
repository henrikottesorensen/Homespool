using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;
using Homespool.Host.Exceptions;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Host.PrusaConnect.DTO.Telemetry;
using Homespool.Host.PrusaConnect.DTO.Transfers;
using Homespool.Host.PrusaConnect.Transfers;
using Homespool.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
using NSubstitute.Core;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrinterConnectionActor"/> - the single-threaded owner of one connection's command
/// state. Covers the same ground the deleted <c>PrinterCommandTransportTests</c> and
/// <c>PrinterCommandCorrelatorTests</c> did (send/ack correlation, one-in-flight, timeout,
/// disconnect-while-pending), plus the message routing to <see cref="ITelemetrySink"/> that used to
/// live in <c>MessageDispatcher</c>.
/// </summary>
public class PrinterConnectionActorTests
{
    /// <summary>
    /// An open connection that copies every frame written to it into <paramref name="sentFrames"/>.
    /// The copy matters: the frame arrives as a <see cref="ReadOnlyMemory{T}"/> over a buffer the
    /// actor is free to reuse, and tests read the command id back out of it afterwards.
    /// </summary>
    private static IPrinterConnection OpenConnection(List<byte[]> sentFrames)
    {
        IPrinterConnection connection = OpenConnection();

        OnSend(connection, call => sentFrames.Add(((ReadOnlyMemory<byte>)call[0]!).ToArray()));

        return connection;
    }

    /// <summary>
    /// Configures what a substitute connection does when a frame is sent to it.
    /// </summary>
    /// <remarks>
    /// Exists to hold the suppression below in one place, over one line, rather than around each
    /// call site. NSubstitute reads the <c>SendAsync</c> here as the member being configured and
    /// never produces a <see cref="ValueTask"/> for anyone to consume - but CA2012 cannot tell that
    /// apart from a real one being dropped, and every form of the API trips it for a
    /// ValueTask-returning member.
    /// </remarks>
    [SuppressMessage("Reliability", "CA2012:Use ValueTasks correctly",
                     Justification = "NSubstitute call specification, not an invocation; no ValueTask is produced to consume.")]
    private static void OnSend(IPrinterConnection connection, Action<CallInfo> onSend) =>
        connection.WhenForAnyArgs(c => c.SendAsync(default, default)).Do(onSend);

    /// <summary>An open connection that accepts and discards whatever is written to it.</summary>
    private static IPrinterConnection OpenConnection()
    {
        IPrinterConnection connection = Substitute.For<IPrinterConnection>();
        connection.IsOpen.Returns(true);

        return connection;
    }

    private static PrinterConnectionActor NewActor(IPrinterConnection connection, ITelemetrySink? sink = null,
        TimeSpan? responseTimeout = null, int printerId = 1, TimeSpan? sendTimeout = null,
        ILogger<PrinterConnectionActor>? logger = null, ITransferContentStore? contentStore = null) =>
        new(printerId, connection, sink ?? Substitute.For<ITelemetrySink>(),
            logger ?? NullLogger<PrinterConnectionActor>.Instance, responseTimeout ?? TimeSpan.FromSeconds(10),
            contentStore ?? Substitute.For<ITransferContentStore>())
        {
            SendTimeout = sendTimeout ?? TimeSpan.FromSeconds(30),
        };

    /// <summary>
    /// A connection whose <c>SendAsync</c> never completes - a peer that is alive but has stopped
    /// draining its socket, which is the one stall nothing else in the system bounds.
    /// </summary>
    /// <remarks>
    /// Deliberately never completed, rather than delayed: the point is that no amount of waiting
    /// helps. Verified against a real socket that this is what happens - a WebSocket writing 256 KiB
    /// to a peer that accepts but never reads stays pending indefinitely, and only disposal ends it.
    /// </remarks>
    [SuppressMessage("Reliability", "CA2012:Use ValueTasks correctly",
                     Justification = "NSubstitute call specification, not an invocation - same case as OnSend above.")]
    private static IPrinterConnection WedgedConnection()
    {
        IPrinterConnection connection = Substitute.For<IPrinterConnection>();
        connection.IsOpen.Returns(true);
        connection.SendAsync(default, default).ReturnsForAnyArgs(_ => new ValueTask(new TaskCompletionSource().Task));

        return connection;
    }

    /// <summary>
    /// Polls until the actor's loop has observably processed a message, instead of a fixed sleep -
    /// the loop runs concurrently with the test, so "posted" and "processed" are different moments.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue("the actor loop should have processed the message by now");
    }

    /// <summary>
    /// A generous ceiling on awaiting anything the actor is supposed to complete - a regression that
    /// leaves a task incomplete (e.g. a pending command not failed on mailbox completion) fails the
    /// test instead of hanging the suite. Same convention as the parsing tests' run ceiling.
    /// </summary>
    private static Task<T> Eventually<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10));

    /// <inheritdoc cref="Eventually{T}"/>
    private static Task Eventually(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));

    /// <summary>The actor assigns the command id internally; the frame it wrote to the connection is
    /// the only place to read it back from (bytes 1-8, 8 hex digits).</summary>
    private static uint CommandIdOf(byte[] frame) =>
        uint.Parse(System.Text.Encoding.ASCII.GetString(frame, 1, 8), System.Globalization.NumberStyles.HexNumber);

    private static InboundEventMessage EventAnswering(uint commandId, Events eventType = Events.Finished, string? reason = null) =>
        new(DateTimeOffset.UtcNow, new EventDTO { Status = "IDLE", EventType = eventType, CommandId = commandId, Reason = reason });

    [Fact]
    public async Task SendCommandAsyncReturnsNotConnectedWhenTheSocketIsClosed()
    {
        // Arrange
        IPrinterConnection connection = Substitute.For<IPrinterConnection>();
        connection.IsOpen.Returns(false);
        PrinterConnectionActor actor = NewActor(connection);

        // Act
        CommandSendResult result = await Eventually(actor.SendCommandAsync(new PausePrint(), CancellationToken.None));

        // Assert
        result.Outcome.Should().Be(CommandSendOutcome.NotConnected);

        actor.Complete();
        await Eventually(actor.Completion);
    }

    [Fact]
    public async Task SendCommandAsyncCompletesWhenTheAnsweringEventArrives()
    {
        // Arrange
        List<byte[]> sentFrames = [];
        RecordingTelemetrySink sink = new();
        PrinterConnectionActor actor = NewActor(OpenConnection(sentFrames), sink);

        Task<CommandSendResult> sendTask = actor.SendCommandAsync(new PausePrint(), CancellationToken.None);
        await WaitUntilAsync(() => sentFrames.Count == 1);

        // Act
        await actor.PostAsync(EventAnswering(CommandIdOf(sentFrames[0])), CancellationToken.None);
        CommandSendResult result = await Eventually(sendTask);

        // Assert
        result.Outcome.Should().Be(CommandSendOutcome.Completed);
        result.Response!.EventType.Should().Be(Events.Finished);
        sentFrames.Should().ContainSingle();

        // Answering a command doesn't consume the event: it must still reach the sink and be
        // persisted like any other (this is how command acks end up in PrinterEvents).
        //
        // Waited for, not read directly. HandleEvent completes the caller before calling the sink,
        // and RunContinuationsAsynchronously means that continuation resumes on another thread - so
        // the await above can return while the loop has not reached the sink yet. Reading straight
        // away passed on an idle machine and failed under load.
        await WaitUntilAsync(() => sink.EventCalls.Count == 1);

        sink.EventCalls[0].printerId.Should().Be(1);

        actor.Complete();
        await Eventually(actor.Completion);
    }

    [Fact]
    public async Task RejectionReasonComesBackVerbatim()
    {
        // Arrange
        List<byte[]> sentFrames = [];
        PrinterConnectionActor actor = NewActor(OpenConnection(sentFrames));

        Task<CommandSendResult> sendTask = actor.SendCommandAsync(new SetPrinterIdle(), CancellationToken.None);
        await WaitUntilAsync(() => sentFrames.Count == 1);

        // Act
        await actor.PostAsync(EventAnswering(CommandIdOf(sentFrames[0]), Events.Rejected, "Can't set idle now"), CancellationToken.None);
        CommandSendResult result = await Eventually(sendTask);

        // Assert
        result.Response!.EventType.Should().Be(Events.Rejected);
        result.Response.Reason.Should().Be("Can't set idle now");

        actor.Complete();
        await Eventually(actor.Completion);
    }

    /// <summary>
    /// A command the printer cannot answer completes as soon as its frame is written, and does not
    /// take the in-flight slot - because nothing would ever free it.
    /// </summary>
    /// <remarks>
    /// <c>RESET_PRINTER</c> is the real case: firmware's handler reboots the machine and the
    /// rejection built after it is annotated "We reach this place only if the reset_printer fails to
    /// execute" (planner.cpp:960-966). A capture shows Prusa's Connect sending it nine times over
    /// 57 s to a printer that had already gone down. Without this the caller would wait out the full
    /// response timeout and be told a working command had failed.
    /// </remarks>
    [Fact]
    public async Task AnUnanswerableCommandCompletesAsDispatchedAndFreesTheSlot()
    {
        // Arrange
        List<byte[]> sentFrames = [];
        PrinterConnectionActor actor = NewActor(OpenConnection(sentFrames));

        // Act
        CommandSendResult result = await Eventually(actor.SendCommandAsync(new ResetPrinter(), CancellationToken.None));

        // Assert
        result.Outcome.Should().Be(CommandSendOutcome.Dispatched);
        result.Response.Should().BeNull("there is no answer to report, and inventing one would lie about the wire");
        sentFrames.Should().ContainSingle("the frame still goes out - only the waiting is skipped");

        // The slot is free: a following command is sent rather than refused as AlreadyInFlight.
        Task<CommandSendResult> next = actor.SendCommandAsync(new PausePrint(), CancellationToken.None);
        await WaitUntilAsync(() => sentFrames.Count == 2);

        actor.Complete();
        (await Eventually(next)).Outcome.Should().Be(CommandSendOutcome.NotConnected);
        await Eventually(actor.Completion);
    }

    [Fact]
    public async Task SecondSendWhileOneIsPendingReturnsAlreadyInFlight()
    {
        // Arrange
        List<byte[]> sentFrames = [];
        PrinterConnectionActor actor = NewActor(OpenConnection(sentFrames));

        Task<CommandSendResult> firstSend = actor.SendCommandAsync(new PausePrint(), CancellationToken.None);
        await WaitUntilAsync(() => sentFrames.Count == 1);

        // Act
        CommandSendResult secondResult = await Eventually(actor.SendCommandAsync(new ResumePrint(), CancellationToken.None));

        // Assert
        secondResult.Outcome.Should().Be(CommandSendOutcome.AlreadyInFlight);
        sentFrames.Should().ContainSingle("the rejected second send must never reach the wire");

        // Cleanup: completing the mailbox resolves the first send too (as NotConnected).
        actor.Complete();
        (await Eventually(firstSend)).Outcome.Should().Be(CommandSendOutcome.NotConnected);
        await Eventually(actor.Completion);
    }

    /// <summary>
    /// The printer disconnecting while a command is awaiting its reply. Where the old model needed a
    /// controller finally-block plus an exception filter to tell this apart from other cancellation
    /// causes, here it is the mailbox completing: the drain fails the pending command as
    /// NotConnected, and there is nothing else it could mean.
    /// </summary>
    [Fact]
    public async Task CompletingTheMailboxWhileACommandIsPendingFailsItAsNotConnected()
    {
        // Arrange
        List<byte[]> sentFrames = [];

        // Long timeout so a genuine timeout can't race the completion below and mask it.
        PrinterConnectionActor actor = NewActor(OpenConnection(sentFrames), responseTimeout: TimeSpan.FromSeconds(30));

        Task<CommandSendResult> sendTask = actor.SendCommandAsync(new PausePrint(), CancellationToken.None);
        await WaitUntilAsync(() => sentFrames.Count == 1);

        // Act
        actor.Complete();
        CommandSendResult result = await Eventually(sendTask);

        // Assert
        result.Outcome.Should().Be(CommandSendOutcome.NotConnected);
        await Eventually(actor.Completion);
    }

    [Fact]
    public async Task SendCommandAsyncAfterTheMailboxIsCompletedReturnsNotConnected()
    {
        // Arrange
        PrinterConnectionActor actor = NewActor(OpenConnection());
        actor.Complete();
        await Eventually(actor.Completion);

        // Act
        CommandSendResult result = await Eventually(actor.SendCommandAsync(new PausePrint(), CancellationToken.None));

        // Assert
        // The message never entered the mailbox (ChannelClosedException path), so nothing will ever
        // answer it - the caller finds out immediately instead of hanging.
        result.Outcome.Should().Be(CommandSendOutcome.NotConnected);
    }

    /// <summary>
    /// The caller's own token (e.g. the HTTP request itself being aborted) is a different case from
    /// disconnect or timeout and must not be swallowed into a CommandSendResult nobody will ever
    /// read - it propagates as an ordinary OperationCanceledException, which ASP.NET Core already
    /// handles.
    /// </summary>
    [Fact]
    public async Task SendCommandAsyncPropagatesCancellationFromTheCallersOwnToken()
    {
        // Arrange
        List<byte[]> sentFrames = [];
        PrinterConnectionActor actor = NewActor(OpenConnection(sentFrames), responseTimeout: TimeSpan.FromSeconds(30));

        using CancellationTokenSource cts = new();

        // Act
        Task<CommandSendResult> sendTask = actor.SendCommandAsync(new PausePrint(), cts.Token);
        await WaitUntilAsync(() => sentFrames.Count == 1);
        await cts.CancelAsync();

        // Assert
        await Assert.ThrowsAsync<TaskCanceledException>(() => Eventually(sendTask));

        actor.Complete();
        await Eventually(actor.Completion);
    }

    [Fact]
    public async Task SendCommandAsyncPropagatesAndDoesNotWedgeTheActorWhenTheConnectionThrows()
    {
        // Arrange
        List<byte[]> sentFrames = [];
        bool failSends = true;

        IPrinterConnection connection = Substitute.For<IPrinterConnection>();
        connection.IsOpen.Returns(true);
        OnSend(connection, call =>
        {
            if (failSends)
            {
                throw new InvalidOperationException("socket gone");
            }

            sentFrames.Add(((ReadOnlyMemory<byte>)call[0]!).ToArray());
        });

        PrinterConnectionActor actor = NewActor(connection);

        // Act
        Func<Task> act = () => actor.SendCommandAsync(new PausePrint(), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Not wedged: the failed send never became pending, so the next command proceeds normally.
        failSends = false;
        Task<CommandSendResult> retry = actor.SendCommandAsync(new PausePrint(), CancellationToken.None);
        await WaitUntilAsync(() => sentFrames.Count == 1);
        await actor.PostAsync(EventAnswering(CommandIdOf(sentFrames[0])), CancellationToken.None);

        (await Eventually(retry)).Outcome.Should().Be(CommandSendOutcome.Completed);

        actor.Complete();
        await Eventually(actor.Completion);
    }

    /// <summary>
    /// A command nobody answers is reported as timed out rather than waited on forever.
    /// </summary>
    /// <remarks>
    /// The deadline is deliberately tight, and can afford to be: nothing has to happen inside it
    /// except the deadline firing. Anything that has to <i>fit</i> in it belongs in
    /// <see cref="ASendAfterATimedOutCommandStillCompletes"/>, which is why that used to be the
    /// second half of this test and no longer is.
    /// </remarks>
    [Fact]
    public async Task SendCommandAsyncReturnsTimedOutWhenNoEventArrivesInTime()
    {
        // Arrange
        PrinterConnectionActor actor = NewActor(OpenConnection(), responseTimeout: TimeSpan.FromMilliseconds(50));

        // Act
        CommandSendResult result = await Eventually(actor.SendCommandAsync(new PausePrint(), CancellationToken.None));

        // Assert
        result.Outcome.Should().Be(CommandSendOutcome.ResponseTimedOut);

        actor.Complete();
        await Eventually(actor.Completion);
    }

    /// <summary>
    /// A timeout does not wedge the actor: the next command reaches the wire and is answered
    /// normally.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="SendCommandAsyncReturnsTimedOutWhenNoEventArrivesInTime"/> on
    /// 2026-07-29 because the two halves want opposite deadlines and an actor has only one. At the
    /// 50 ms that makes the timeout itself quick, this round trip - write the frame, poll for it,
    /// post the answer, let the loop reach it - had that <i>whole</i> budget to fit into, and on a loaded
    /// machine it does not: the retry came back ResponseTimedOut in 2 of 5 runs under CPU
    /// saturation, reported as a wedged actor when nothing was wrong. Measured under the same load
    /// that reproduces that, the round trip takes 10-26 ms, so half a second is roughly twenty times
    /// the worst of it - and the cost is paid by the deliberate timeout above rather than by the
    /// half being asserted, which is the right way round.
    /// </remarks>
    [Fact]
    public async Task ASendAfterATimedOutCommandStillCompletes()
    {
        // Arrange - the timeout here is the premise, not the assertion; it is asserted so that a
        // regression turning it into something else fails as itself rather than as the retry.
        List<byte[]> sentFrames = [];
        PrinterConnectionActor actor = NewActor(OpenConnection(sentFrames), responseTimeout: TimeSpan.FromMilliseconds(500));

        CommandSendResult timedOut = await Eventually(actor.SendCommandAsync(new PausePrint(), CancellationToken.None));
        timedOut.Outcome.Should().Be(CommandSendOutcome.ResponseTimedOut);

        // Act
        Task<CommandSendResult> retry = actor.SendCommandAsync(new ResumePrint(), CancellationToken.None);
        await WaitUntilAsync(() => sentFrames.Count == 2);
        await actor.PostAsync(EventAnswering(CommandIdOf(sentFrames[1])), CancellationToken.None);

        // Assert
        (await Eventually(retry)).Outcome.Should().Be(CommandSendOutcome.Completed);

        actor.Complete();
        await Eventually(actor.Completion);
    }

    [Fact]
    public async Task EventWithNonMatchingCommandIdLeavesThePendingCommandOutstanding()
    {
        // Arrange
        List<byte[]> sentFrames = [];
        RecordingTelemetrySink sink = new();
        PrinterConnectionActor actor = NewActor(OpenConnection(sentFrames), sink);

        Task<CommandSendResult> sendTask = actor.SendCommandAsync(new PausePrint(), CancellationToken.None);
        await WaitUntilAsync(() => sentFrames.Count == 1);
        uint commandId = CommandIdOf(sentFrames[0]);

        // Act
        await actor.PostAsync(EventAnswering(unchecked(commandId + 1)), CancellationToken.None);
        await WaitUntilAsync(() => sink.EventCalls.Count == 1);

        // Assert
        // The unrelated event must not be mistaken for the answer - but it is still an event, and
        // still reaches the sink.
        sendTask.IsCompleted.Should().BeFalse();

        // The genuine answer still lands afterwards.
        await actor.PostAsync(EventAnswering(commandId), CancellationToken.None);
        (await Eventually(sendTask)).Outcome.Should().Be(CommandSendOutcome.Completed);

        actor.Complete();
        await Eventually(actor.Completion);
    }

    [Fact]
    public async Task TelemetryIsForwardedToTheSink()
    {
        // Arrange
        RecordingTelemetrySink sink = new();
        PrinterConnectionActor actor = NewActor(OpenConnection(), sink, printerId: 7);
        DateTimeOffset receivedAt = DateTimeOffset.UtcNow;

        // Act
        await actor.PostAsync(new InboundTelemetryMessage(receivedAt, new TelemetryDTO { Status = "PRINTING" }), CancellationToken.None);
        await WaitUntilAsync(() => sink.TelemetryCalls.Count == 1);

        // Assert
        sink.TelemetryCalls[0].printerId.Should().Be(7);
        sink.TelemetryCalls[0].receivedAt.Should().Be(receivedAt);
        sink.TelemetryCalls[0].telemetry.Status.Should().Be("PRINTING");

        actor.Complete();
        await Eventually(actor.Completion);
    }

    [Fact]
    public async Task TransferRequestIsAcceptedAndProducesNoSinkCalls()
    {
        // Arrange
        RecordingTelemetrySink sink = new();
        PrinterConnectionActor actor = NewActor(OpenConnection(), sink);

        // Act
        // Nothing to persist for this shape - the transfer feature isn't built. Posting telemetry
        // afterwards and waiting for it proves the transfer message was processed (FIFO), not
        // merely ignored in the mailbox.
        await actor.PostAsync(new InboundTransferRequestMessage(DateTimeOffset.UtcNow, new InlineRequestDTO()), CancellationToken.None);
        await actor.PostAsync(new InboundTelemetryMessage(DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING" }), CancellationToken.None);
        await WaitUntilAsync(() => sink.TelemetryCalls.Count == 1);

        // Assert
        sink.EventCalls.Should().BeEmpty();

        actor.Complete();
        await Eventually(actor.Completion);
    }

    /// <summary>
    /// A command whose caller has given up is not sent, and does not occupy the in-flight slot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Posting to the mailbox and awaiting the reply are two separate steps, so cancelling only ends
    /// the wait - <c>WaitAsync</c> produces a derived task and leaves both the underlying completion
    /// and the queued message untouched. Without the caller's token travelling with the message, the
    /// loop would pick it up afterwards and put it on the wire: an aborted request could still stop a
    /// running print, and would then hold the one in-flight slot, so the operator's next click came
    /// back AlreadyInFlight because of a command they cancelled.
    /// </para>
    /// <para>
    /// The old PrinterCommandTransport had no such gap - it sent inline, on the caller's stack, with
    /// the caller's token. The actor's separation of posting from executing is what opens it.
    /// </para>
    /// <para>
    /// Nothing here depends on timing. The loop is held inside the sink, so the command provably sits
    /// in the mailbox; <c>SendCommandAsync</c> runs synchronously until its first real suspension and
    /// a channel write with free capacity is not one, so the message is queued by the time it hands
    /// back a task; and a later message observed through the sink proves, by FIFO, that the command
    /// was processed before the assertion runs.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ACancelledCallersCommandIsNeitherSentNorLeftInFlight()
    {
        // Arrange
        List<byte[]> sentFrames = [];
        using GatedTelemetrySink sink = new();
        PrinterConnectionActor actor = NewActor(OpenConnection(sentFrames), sink);

        await actor.PostAsync(new InboundTelemetryMessage(DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING" }), CancellationToken.None);
        await WaitUntilAsync(() => sink.IsHeld);

        using CancellationTokenSource cts = new();

        Task<CommandSendResult> abandoned = actor.SendCommandAsync(new PausePrint(), cts.Token);

        // Act - the caller gives up while its command is still queued behind the held message.
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Eventually(abandoned));

        sink.Release();

        await actor.PostAsync(new InboundTelemetryMessage(DateTimeOffset.UtcNow, new TelemetryDTO { Status = "IDLE" }), CancellationToken.None);
        await WaitUntilAsync(() => sink.TelemetryCount == 2);

        // Assert
        sentFrames.Should().BeEmpty("a command whose caller has gone must not still reach the printer");

        // And the slot is free: the next command goes out rather than coming back AlreadyInFlight.
        Task<CommandSendResult> next = actor.SendCommandAsync(new ResumePrint(), CancellationToken.None);
        await WaitUntilAsync(() => sentFrames.Count == 1);
        await actor.PostAsync(EventAnswering(CommandIdOf(sentFrames[0])), CancellationToken.None);

        (await Eventually(next)).Outcome.Should().Be(CommandSendOutcome.Completed,
            "the abandoned command must not have taken the one in-flight slot with it");

        actor.Complete();
        await Eventually(actor.Completion);
    }

    /// <summary>
    /// A command still queued when teardown begins is refused, not sent.
    /// </summary>
    /// <remarks>
    /// The drain processes whatever the mailbox still holds, and a queued command would otherwise
    /// pass every check and go out on the wire - on the clean-shutdown path the socket is still open
    /// when this runs, so <c>IsOpen</c> does not catch it. It would then become the pending command
    /// and be failed as NotConnected by the loop's finally: the operator told the printer was
    /// unreachable by a command that had just executed on it.
    /// </remarks>
    [Fact]
    public async Task ACommandStillQueuedWhenTeardownBeginsIsNotSent()
    {
        // Arrange
        List<byte[]> sentFrames = [];
        using GatedTelemetrySink sink = new();
        PrinterConnectionActor actor = NewActor(OpenConnection(sentFrames), sink);

        await actor.PostAsync(new InboundTelemetryMessage(DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING" }), CancellationToken.None);
        await WaitUntilAsync(() => sink.IsHeld);

        // Queued behind the held message, exactly as a click landing during shutdown would be.
        Task<CommandSendResult> queued = actor.SendCommandAsync(new PausePrint(), CancellationToken.None);

        // Act
        actor.Complete();
        sink.Release();

        // Assert
        (await Eventually(queued)).Outcome.Should().Be(CommandSendOutcome.NotConnected);

        await Eventually(actor.Completion);

        sentFrames.Should().BeEmpty("a connection being torn down must not still carry the command to the printer");
    }

    /// <summary>
    /// An ack already in the mailbox answers its command, even if the loop reaches it late.
    /// </summary>
    /// <remarks>
    /// The deadline governs waiting, not processing. Checking it before draining meant a reply that
    /// arrived comfortably inside the timeout could still be reported TimedOut because the loop was
    /// busy - and the ack would then be persisted as an ordinary unmatched event, so the record
    /// would disagree with what the caller was told.
    /// </remarks>
    [Fact]
    public async Task AnAckAlreadyInTheMailboxBeatsTheDeadline()
    {
        // Arrange - the timeout has to outlast the setup below and still expire while the loop is
        // held, so it is generous on both sides: everything before the hold takes milliseconds, and
        // the hold is 1.5x the timeout. A tighter margin failed under parallel load, when the
        // deadline could expire before the loop was even parked.
        List<byte[]> sentFrames = [];
        using GatedTelemetrySink sink = new();
        PrinterConnectionActor actor = NewActor(OpenConnection(sentFrames), sink, responseTimeout: TimeSpan.FromSeconds(1));

        Task<CommandSendResult> send = actor.SendCommandAsync(new PausePrint(), CancellationToken.None);
        await WaitUntilAsync(() => sentFrames.Count == 1);

        // Park the loop, then let the printer's answer arrive behind the held message.
        await actor.PostAsync(new InboundTelemetryMessage(DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING" }), CancellationToken.None);
        await WaitUntilAsync(() => sink.IsHeld);
        await actor.PostAsync(EventAnswering(CommandIdOf(sentFrames[0])), CancellationToken.None);

        // Act - the deadline passes while the ack sits in the mailbox, unread.
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        sink.Release();

        // Assert
        (await Eventually(send)).Outcome.Should().Be(CommandSendOutcome.Completed,
            "the reply arrived within the timeout; only the loop was late to it");

        actor.Complete();
        await Eventually(actor.Completion);
    }

    /// <summary>
    /// A write that never completes gives up on the <i>connection</i>, not just the command: the
    /// caller is told the outcome is unknown, and the actor stops accepting work so the read loop
    /// unwinds into teardown.
    /// </summary>
    /// <remarks>
    /// The one stall nothing else bounds. The response timeout cannot cover it - that clock starts
    /// only once the write returns - and the read loop is by then parked in <c>PostAsync</c> against
    /// a full mailbox, so it never notices the peer either. Without this the request, socket and
    /// actor leak permanently and silently, while the printer's own 60s socket timeout lets it
    /// reconnect and look healthy.
    /// </remarks>
    [Fact]
    public async Task AWriteThatNeverCompletesAbandonsTheConnection()
    {
        // Arrange
        PrinterConnectionActor actor = NewActor(WedgedConnection(), sendTimeout: TimeSpan.FromMilliseconds(150));

        // Act - wrapped in Eventually, not awaited bare: if the send deadline ever stops firing this
        // call never returns at all, and a hanging test is worse than a failing one.
        Func<Task> send = () => Eventually(actor.SendCommandAsync(new PausePrint(), CancellationToken.None));

        // Assert - told, and told honestly. Not PrinterNotConnectedException, which would claim the
        // command never left; not CommandResponseTimedOutException, which would claim it arrived and went
        // unanswered. Neither is known here.
        await send.Should().ThrowAsync<CommandSendTimedOutException>();

        // And the actor gave up on the connection, not merely on the command. That is the load-
        // bearing half: completing the mailbox is what ends the read loop, which reaches the
        // teardown that disposes the socket - the only thing that can end the abandoned write.
        await Eventually(actor.Completion);
    }

    /// <summary>
    /// A stream of messages whose handling throws logs one throttled Error, not one per message.
    /// The catch-all guards the unforeseen - no known message type throws today - but if a crafted
    /// message ever finds a throwing path, an attacker could otherwise drive an Error-with-stack at
    /// wire rate (the blast test's 1.0 GB log, in Error form).
    /// </summary>
    [Fact]
    public async Task ABurstOfPoisonMessagesLogsOneThrottledError()
    {
        // Arrange - a sink that rejects everything stands in for whatever unforeseen throw the
        // catch-all exists to survive.
        ITelemetrySink sink = Substitute.For<ITelemetrySink>();
        sink.WhenForAnyArgs(s => s.Enqueue(default, default, (TelemetryDTO)null!))
            .Do(_ => throw new InvalidOperationException("poison"));

        FakeLogger<PrinterConnectionActor> logger = new();
        PrinterConnectionActor actor = NewActor(OpenConnection(), sink, logger: logger);

        // Act
        for (int i = 0; i < 5; i++)
        {
            await actor.PostAsync(new InboundTelemetryMessage(DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING" }), CancellationToken.None);
        }

        actor.Complete();
        await Eventually(actor.Completion);

        // Assert - the loop survived all five (Completion ran to the drain, not to a fault), and
        // exactly one Error was written for the burst.
        logger.Collector.GetSnapshot().Count(record => record.Level == LogLevel.Error)
            .Should().Be(1, "five poison messages inside one throttle window must produce one Error, not five");
    }

    /// <summary>
    /// Blocks the actor's loop on demand. <see cref="ITelemetrySink.Enqueue(int,DateTimeOffset,TelemetryDTO)"/> is synchronous and
    /// called from the loop, which makes it the one place a test can hold the loop still without
    /// guessing at timing.
    /// </summary>
    private sealed class GatedTelemetrySink : ITelemetrySink, IDisposable
    {
        private readonly ManualResetEventSlim _gate = new(false);
        private readonly object _sync = new();
        private int _telemetryCount;

        /// <summary>True while the loop is parked inside this sink.</summary>
        /// <remarks>
        /// A field rather than a property because it is <c>volatile</c>, and that is load-bearing: the
        /// actor loop writes it while the test thread reads it. C# has no volatile property, so the
        /// property SA1401 asks for would silently drop the guarantee this exists to provide.
        /// </remarks>
        [SuppressMessage("Design", "SA1401:Fields should be private",
                         Justification = "Volatile cross-thread flag on a test double; a property cannot carry the volatile semantics.")]
        public volatile bool IsHeld;

        public int TelemetryCount
        {
            get
            {
                lock (_sync)
                {
                    return _telemetryCount;
                }
            }
        }

        public void Release() => _gate.Set();

        public void Enqueue(int printerId, DateTimeOffset receivedAt, TelemetryDTO telemetry)
        {
            lock (_sync)
            {
                _telemetryCount++;
            }

            if (!_gate.IsSet)
            {
                IsHeld = true;
                _gate.Wait();
                IsHeld = false;
            }
        }

        public void Enqueue(int printerId, DateTimeOffset receivedAt, EventDTO eventDto)
        {
        }

        // Disposed by the test once the actor's loop has finished with it, unlike the production
        // write lock: this gate really does allocate a wait handle, and its lifetime ends with the
        // test rather than racing anything.
        public void Dispose() => _gate.Dispose();
    }

    // ---------- Outbound command logging ----------

    /// <summary>
    /// Both halves of the pair, in one test because the correlation between them is the point: a
    /// "sent" line nobody can match to an answer is barely better than no line at all.
    /// </summary>
    [Fact]
    public async Task SendingAndAnsweringACommandAreBothLogged()
    {
        // Arrange
        List<byte[]> sentFrames = [];
        FakeLogger<PrinterConnectionActor> logger = new();
        PrinterConnectionActor actor = NewActor(OpenConnection(sentFrames), logger: logger);

        Task<CommandSendResult> sendTask = actor.SendCommandAsync(new PausePrint(), CancellationToken.None);
        await WaitUntilAsync(() => sentFrames.Count == 1);

        // Act
        uint commandId = CommandIdOf(sentFrames[0]);
        await actor.PostAsync(EventAnswering(commandId, Events.Finished, null), CancellationToken.None);
        await Eventually(sendTask);

        // Assert
        string[] messages = logger.Collector.GetSnapshot()
            .Where(record => record.Level == LogLevel.Debug)
            .Select(record => record.Message)
            .ToArray();

        messages.Should().Contain(message => message.Contains("sent PAUSE_PRINT", StringComparison.Ordinal)
                                             && message.Contains(commandId.ToString(), StringComparison.Ordinal));

        messages.Should().Contain(message => message.Contains("answered with Finished", StringComparison.Ordinal)
                                             && message.Contains(commandId.ToString(), StringComparison.Ordinal)
                                             && message.Contains("ms", StringComparison.Ordinal));

        actor.Complete();
        await Eventually(actor.Completion);
    }

    /// <summary>
    /// Firmware's own rejection text is the usual explanation of a <c>Rejected</c>, and it is
    /// composed by the printer (<c>JC</c>'s macro strings) rather than by us - so it belongs in the
    /// line rather than only in the caller's return value.
    /// </summary>
    [Fact]
    public async Task ARejectionsReasonReachesTheLog()
    {
        // Arrange
        List<byte[]> sentFrames = [];
        FakeLogger<PrinterConnectionActor> logger = new();
        PrinterConnectionActor actor = NewActor(OpenConnection(sentFrames), logger: logger);

        Task<CommandSendResult> sendTask = actor.SendCommandAsync(new SetPrinterIdle(), CancellationToken.None);
        await WaitUntilAsync(() => sentFrames.Count == 1);

        // Act
        await actor.PostAsync(EventAnswering(CommandIdOf(sentFrames[0]), Events.Rejected, "Can't set idle now"),
                              CancellationToken.None);
        await Eventually(sendTask);

        // Assert
        logger.Collector.GetSnapshot()
              .Should().Contain(record => record.Message.Contains("Can't set idle now", StringComparison.Ordinal));

        actor.Complete();
        await Eventually(actor.Completion);
    }

    /// <summary>
    /// A command's arguments never reach the log - only its wire name and id.
    /// <see cref="StartConnectDownload.Hash"/> is the live case rather than a hypothetical one: it is
    /// today the capability token that lets whoever holds it fetch the file
    /// (<c>notes/backlog.md</c>, "once ownership is enforced by path, the wire token no longer has to
    /// be unguessable"), and <c>SET_TOKEN</c> will be a sharper one when token rotation is built.
    /// </summary>
    [Fact]
    public async Task ACommandsArgumentsAreNeverLogged()
    {
        // Arrange
        List<byte[]> sentFrames = [];
        FakeLogger<PrinterConnectionActor> logger = new();
        PrinterConnectionActor actor = NewActor(OpenConnection(sentFrames), logger: logger);

        StartConnectDownload command = new()
        {
            Path = "/usb/secret-model.bgcode",
            Hash = "CAPABILITY-TOKEN-abc123",
            TeamId = 7,
            OriginalSize = 4096,
        };

        // Act
        Task<CommandSendResult> sendTask = actor.SendCommandAsync(command, CancellationToken.None);
        await WaitUntilAsync(() => sentFrames.Count == 1);

        // Assert - the frame carries them, the log does not
        sentFrames[0].Should().NotBeEmpty();

        string log = string.Join('\n', logger.Collector.GetSnapshot().Select(record => record.Message));

        log.Should().Contain("START_CONNECT_DOWNLOAD", "the wire name is the whole point of the line");
        log.Should().NotContain("CAPABILITY-TOKEN-abc123");
        log.Should().NotContain("secret-model.bgcode");

        actor.Complete();
        await Eventually(sendTask);
        await Eventually(actor.Completion);
    }

    /// <summary>
    /// The same rule against the command where breaking it would be worst. <c>SET_TOKEN</c> carries a
    /// printer's replacement credential, and its own remarks describe it as remote credential
    /// management - so logging its arguments would write the new secret to disk at the exact moment
    /// it is issued, in the log an operator is most likely to be reading and sharing when a printer
    /// is misbehaving.
    /// </summary>
    /// <remarks>
    /// This case only became possible when <c>SetToken</c> became <see cref="ISendableCommand"/>.
    /// While it was a hollow marker the assertion could not fail whatever the code did, which is why
    /// <see cref="ACommandsArgumentsAreNeverLogged"/> was written against
    /// <see cref="StartConnectDownload"/> instead.
    /// </remarks>
    [Fact]
    public async Task ARotatedTokenIsNeverLogged()
    {
        // Arrange
        List<byte[]> sentFrames = [];
        FakeLogger<PrinterConnectionActor> logger = new();
        PrinterConnectionActor actor = NewActor(OpenConnection(sentFrames), logger: logger);

        // Act
        Task<CommandSendResult> sendTask = actor.SendCommandAsync(
            new SetToken { Token = "replacement-token99" }, CancellationToken.None);

        await WaitUntilAsync(() => sentFrames.Count == 1);

        // Assert - it really is in the frame, and really is not in the log
        System.Text.Encoding.ASCII.GetString(sentFrames[0])
              .Should().Contain("replacement-token99", "the printer has to receive it, or the test proves nothing");

        string log = string.Join('\n', logger.Collector.GetSnapshot().Select(record => record.Message));

        log.Should().Contain("SET_TOKEN");
        log.Should().NotContain("replacement-token99");

        actor.Complete();
        await Eventually(sendTask);
        await Eventually(actor.Completion);
    }

    /// <summary>Records every call instead of acting on it - captured-value assertions
    /// (<c>sink.TelemetryCalls[0].Telemetry.Status</c>) fail far more legibly than an
    /// <c>Arg.Is&lt;&gt;</c> lambda, which is why this stays a class rather than a substitute.</summary>
    private sealed class RecordingTelemetrySink : ITelemetrySink
    {
        public List<(int printerId, DateTimeOffset receivedAt, TelemetryDTO telemetry)> TelemetryCalls { get; } = [];

        public List<(int printerId, DateTimeOffset receivedAt, EventDTO eventDto)> EventCalls { get; } = [];

        public void Enqueue(int printerId, DateTimeOffset receivedAt, TelemetryDTO telemetry)
        {
            TelemetryCalls.Add((printerId, receivedAt, telemetry));
        }

        public void Enqueue(int printerId, DateTimeOffset receivedAt, EventDTO eventDto)
        {
            EventCalls.Add((printerId, receivedAt, eventDto));
        }
    }
}
