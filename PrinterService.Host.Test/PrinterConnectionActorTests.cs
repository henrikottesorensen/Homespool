using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using PrinterService.Host.PrusaConnect;
using PrinterService.Host.PrusaConnect.Commands;
using PrinterService.Host.PrusaConnect.DTO.EventMessages;
using PrinterService.Host.PrusaConnect.DTO.Telemetry;
using PrinterService.Model;

namespace PrinterService.Host.Test;

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

        // Configuring a substitute, not calling it: NSubstitute reads this "call" as the member to
        // set up and never produces a ValueTask to consume, which CA2012 cannot distinguish from a
        // real one being dropped. Every form of its API trips the rule for a ValueTask member.
#pragma warning disable CA2012
        connection.WhenForAnyArgs(c => c.SendAsync(default, default))
                  .Do(call => sentFrames.Add(((ReadOnlyMemory<byte>)call[0]!).ToArray()));
#pragma warning restore CA2012

        return connection;
    }

    /// <summary>An open connection that accepts and discards whatever is written to it.</summary>
    private static IPrinterConnection OpenConnection()
    {
        IPrinterConnection connection = Substitute.For<IPrinterConnection>();
        connection.IsOpen.Returns(true);

        return connection;
    }

    private static PrinterConnectionActor NewActor(IPrinterConnection connection, ITelemetrySink? sink = null,
        TimeSpan? responseTimeout = null, int printerId = 1) =>
        new(printerId, connection, sink ?? Substitute.For<ITelemetrySink>(),
            NullLogger<PrinterConnectionActor>.Instance, responseTimeout ?? TimeSpan.FromSeconds(10));

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
        sink.EventCalls.Should().ContainSingle();
        sink.EventCalls[0].PrinterId.Should().Be(1);

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
#pragma warning disable CA2012 // Substitute configuration, not a dropped ValueTask - see above.
        connection.WhenForAnyArgs(c => c.SendAsync(default, default))
                  .Do(call =>
                  {
                      if (failSends)
                      {
                          throw new InvalidOperationException("socket gone");
                      }

                      sentFrames.Add(((ReadOnlyMemory<byte>)call[0]!).ToArray());
                  });
#pragma warning restore CA2012

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

    [Fact]
    public async Task SendCommandAsyncReturnsTimedOutWhenNoEventArrivesInTime()
    {
        // Arrange
        List<byte[]> sentFrames = [];
        PrinterConnectionActor actor = NewActor(OpenConnection(sentFrames), responseTimeout: TimeSpan.FromMilliseconds(50));

        // Act
        CommandSendResult result = await Eventually(actor.SendCommandAsync(new PausePrint(), CancellationToken.None));

        // Assert
        result.Outcome.Should().Be(CommandSendOutcome.TimedOut);

        // Not wedged: a new send for the same printer reaches the wire immediately.
        Task<CommandSendResult> retry = actor.SendCommandAsync(new ResumePrint(), CancellationToken.None);
        await WaitUntilAsync(() => sentFrames.Count == 2);
        await actor.PostAsync(EventAnswering(CommandIdOf(sentFrames[1])), CancellationToken.None);

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
        sink.TelemetryCalls[0].PrinterId.Should().Be(7);
        sink.TelemetryCalls[0].ReceivedAt.Should().Be(receivedAt);
        sink.TelemetryCalls[0].Telemetry.Status.Should().Be("PRINTING");

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
        await actor.PostAsync(new InboundTransferRequestMessage(), CancellationToken.None);
        await actor.PostAsync(new InboundTelemetryMessage(DateTimeOffset.UtcNow, new TelemetryDTO { Status = "PRINTING" }), CancellationToken.None);
        await WaitUntilAsync(() => sink.TelemetryCalls.Count == 1);

        // Assert
        sink.EventCalls.Should().BeEmpty();

        actor.Complete();
        await Eventually(actor.Completion);
    }

    /// <summary>Records every call instead of acting on it - captured-value assertions
    /// (<c>sink.TelemetryCalls[0].Telemetry.Status</c>) fail far more legibly than an
    /// <c>Arg.Is&lt;&gt;</c> lambda, which is why this stays a class rather than a substitute.</summary>
    private sealed class RecordingTelemetrySink : ITelemetrySink
    {
        public List<(int PrinterId, DateTimeOffset ReceivedAt, TelemetryDTO Telemetry)> TelemetryCalls { get; } = [];

        public List<(int PrinterId, DateTimeOffset ReceivedAt, EventDTO Event)> EventCalls { get; } = [];

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
