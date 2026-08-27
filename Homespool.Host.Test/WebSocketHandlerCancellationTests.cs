using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

/// <summary>
/// The two ways <c>WebSocketHandler</c>'s read loop ends when its token is cancelled - the path the
/// controller relies on to stop a printer connection at host shutdown, and the one that used to hang
/// until Kestrel's shutdown timeout killed the request.
/// </summary>
/// <remarks>
/// <para>
/// Both exits are pinned here rather than end-to-end because both are <i>deterministic at this
/// level</i> and neither is above it. Given a reader with nothing to read, the loop can only be
/// parked in <c>ReadAsync</c>, so cancelling then always takes the throwing exit; given an
/// already-cancelled token, the <c>while</c> condition always ends the loop before the first read.
/// Racing those two outcomes against each other through a real socket would test the race rather
/// than the behaviour, and a flaky test on the shutdown path is worse than none.
/// </para>
/// <para>
/// Deliberately <b>not</b> covered here: cancelling a <c>StreamPipeReader</c> over a
/// <c>WebSocketStream</c> aborts the socket. That is the stream's behaviour, not the handler's, and
/// asserting it against a plain <see cref="Pipe"/> would only confirm our model of it. Nor is
/// shutdown <i>promptness</i>.
/// </para>
/// </remarks>
public class WebSocketHandlerCancellationTests
{
    /// <summary>The shipped defaults - a 1 MiB message cap, which nothing here approaches.</summary>
    private static readonly IOptions<PrusaConnectOptions> DefaultOptions =
        Options.Create(new PrusaConnectOptions());

    private static WebSocketHandler NewHandler(RecordingMessageDispatcher dispatcher)
    {
        return new(NullLogger<WebSocketHandler>.Instance, dispatcher, DefaultOptions);
    }

    /// <summary>
    /// Cancelling while the loop waits for bytes ends it by throwing, which is what the controller
    /// catches to close the connection down instead of logging a 500.
    /// </summary>
    [Fact]
    public async Task CancellingWhileWaitingForBytesEndsTheLoop()
    {
        // Arrange
        Pipe wire = new();
        RecordingMessageDispatcher dispatcher = new();
        using CancellationTokenSource cts = new();

        Task run = NewHandler(dispatcher)
            .HandlePrusaWebsocket(wire.Reader, printerId: 1, Substitute.For<IPrinterConnectionActor>(), cts.Token);

        // Nothing is ever written, so once the loop has started there is exactly one place it can
        // be: awaiting ReadAsync. No race to lose - only a moment to let it get there.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        run.IsCompleted.Should().BeFalse("the loop should still be waiting for bytes that never come");

        // Act
        await cts.CancelAsync();

        // Assert
        Func<Task> act = async () => await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a cancelled read is how the connection stops when the host is shutting down");

        dispatcher.Received.Should().BeEmpty();
    }

    /// <summary>
    /// A token already cancelled when the handler is entered ends the loop by its condition instead,
    /// returning normally - the exit that reaches the controller as an ordinary EOF and closes the
    /// socket with <c>NormalClosure</c> rather than dropping it.
    /// </summary>
    [Fact]
    public async Task AnAlreadyCancelledTokenReturnsWithoutReading()
    {
        // Arrange
        Pipe wire = new();
        RecordingMessageDispatcher dispatcher = new();

        // A whole message is waiting: proof the loop stopped because the token said so, not because
        // there was nothing to do.
        await wire.Writer.WriteAsync(Encoding.UTF8.GetBytes("""{"state":"IDLE"}"""), TestContext.Current.CancellationToken);

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        // Act
        Func<Task> act = async () =>
            await NewHandler(dispatcher)
                  .HandlePrusaWebsocket(wire.Reader, printerId: 1, Substitute.For<IPrinterConnectionActor>(), cts.Token)
                  .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        await act.Should().NotThrowAsync(
            "the loop condition ends the run before the first read, which is an ordinary return");

        dispatcher.Received.Should().BeEmpty("nothing should be read once cancellation has been asked for");
    }

    /// <inheritdoc cref="WebSocketHandlerParsingTests"/>
    private sealed class RecordingMessageDispatcher()
        : MessageDispatcher(NullLogger<MessageDispatcher>.Instance,
                            new UnknownFieldTracker(NullLogger<UnknownFieldTracker>.Instance),
                            TimeProvider.System)
    {
        public List<string> Received { get; } = [];

        public override ConnectionMessage? Classify(int printerId, JsonElement root)
        {
            Received.Add(root.GetRawText());

            return null;
        }
    }
}
