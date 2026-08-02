using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;

using NSubstitute;

using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Transfers;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrinterConnectionSession"/>'s teardown sequence - the six ordered steps that used to
/// live in <c>PrusaConnectPrinterController.ConnectWebSocket</c>'s <c>finally</c>, where nothing but
/// comments enforced them because nothing could reach them without a real WebSocket upgrade.
/// </summary>
/// <remarks>
/// <para>
/// Every case here pins an invariant that cost a defect on 2026-07-25. They are unit tests, with no
/// <c>HttpContext</c> and no socket: the read loop's end is supplied by a stub handler, so each way
/// a connection can finish is produced directly rather than raced for.
/// </para>
/// <para>
/// Unregister-before-close is asserted as the invariant it actually is - the connection is no longer
/// reachable through the registry when the close frame goes out - rather than as a call order. That
/// uses the real <see cref="PrinterConnectionRegistry"/>, which is what a command would consult, so
/// the test fails for the reason the bug would have happened rather than for a sequence that merely
/// implies it.
/// </para>
/// </remarks>
public class PrinterConnectionSessionTests
{
    private const int PrinterId = 7;

    /// <summary>
    /// Short, because two of the cases below deliberately wait it out. The production default (5s)
    /// is generous for a mailbox holding seconds of traffic; nothing here depends on the value.
    /// </summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMilliseconds(150);

    private readonly PrinterConnectionRegistry _registry = new(NullLogger<PrinterConnectionRegistry>.Instance);
    private readonly FakeLogger<PrinterConnectionSession> _logger = new();

    private IReadOnlyList<FakeLogRecord> LogRecords => _logger.Collector.GetSnapshot();

    /// <summary>An actor whose mailbox drains the moment it is completed - the ordinary case.</summary>
    private static IPrinterConnectionActor DrainedActor()
    {
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.Completion.Returns(Task.CompletedTask);

        return actor;
    }

    private PrinterConnectionSession NewSession(WebSocketHandler handler, IPrinterConnectionActor actor)
    {
        return new(handler, _registry, new StubActorFactory(actor), _logger)
        {
            ActorDrainTimeout = DrainTimeout,
        };
    }

    /// <summary>
    /// Runs a session whose read loop ends the way <paramref name="handlerEnd"/> says, over a pipe
    /// nothing ever writes to.
    /// </summary>
    private (FakeConnection connection, Func<Task> run) Arrange(Func<Task> handlerEnd,
                                                                IPrinterConnectionActor? actor = null)
    {
        FakeConnection connection = new(_registry);
        Pipe wire = new();
        PrinterConnectionSession session = NewSession(new StubWebSocketHandler(handlerEnd), actor ?? DrainedActor());

        return (connection, () => session.RunAsync(PrinterId, connection, wire.Reader, CancellationToken.None));
    }

    /// <summary>
    /// The printer closed its end: the handler returns, and the connection is closed normally. Also
    /// the base case for unregister-before-close.
    /// </summary>
    [Fact]
    public async Task HandlerReturningClosesNormallyAfterUnregistering()
    {
        // Arrange
        (FakeConnection connection, Func<Task> run) = Arrange(() => Task.CompletedTask);

        // Act
        await run();

        // Assert
        connection.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
        connection.WasRegisteredAtClose.Should().BeFalse(
            "a command that passes IsOpen while the connection is still in the registry can start writing, and the close frame is a write too");
        _registry.IsConnected(PrinterId).Should().BeFalse();
    }

    /// <summary>
    /// Cancellation landing inside a read throws out of the handler. That is an ordinary end to a
    /// WebSocket request - shutdown, or Kestrel aborting it - so it must not escape, and must not be
    /// logged as a fault.
    /// </summary>
    [Fact]
    public async Task CancellationClosesNormallyAndIsNotRethrown()
    {
        // Arrange
        (FakeConnection connection, Func<Task> run) =
            Arrange(() => throw new OperationCanceledException());

        // Act
        Func<Task> act = run;

        // Assert
        await act.Should().NotThrowAsync();
        connection.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
        connection.WasRegisteredAtClose.Should().BeFalse();
        LogRecords.Should().OnlyContain(r => r.Level == LogLevel.Debug,
            "an aborted or shutting-down connection is not a fault");
    }

    /// <summary>
    /// The handler's contract: malformed JSON is a protocol violation. The close says so, and the
    /// exception still surfaces to the caller.
    /// </summary>
    [Fact]
    public async Task MalformedJsonClosesWithPolicyViolationAndStillPropagates()
    {
        // Arrange
        (FakeConnection connection, Func<Task> run) =
            Arrange(() => throw new JsonException("malformed"));

        // Act
        Func<Task> act = run;

        // Assert
        await act.Should().ThrowAsync<JsonException>();
        connection.CloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
        connection.WasRegisteredAtClose.Should().BeFalse();
    }

    /// <summary>
    /// Printers drop off the network without a close handshake all the time. Routine, not a fault -
    /// and it must not escape into the request pipeline as a 500.
    /// </summary>
    [Fact]
    public async Task AbruptDisconnectIsNotRethrown()
    {
        // Arrange
        (FakeConnection connection, Func<Task> run) =
            Arrange(() => throw new WebSocketException("peer vanished"));

        // Act
        Func<Task> act = run;

        // Assert
        await act.Should().NotThrowAsync();
        connection.WasRegisteredAtClose.Should().BeFalse();
        LogRecords.Should().OnlyContain(r => r.Level == LogLevel.Debug);
    }

    /// <summary>
    /// The one step no token cancels is the actor loop's socket write, so the drain wait is bounded.
    /// An unbounded wait here would hold the request open with no ceiling at all - the shutdown
    /// stall the connection's linked ApplicationStopping token exists to prevent, without even
    /// Kestrel's 30s limit. Abandoning the actor is the deliberate outcome, and the close still goes.
    /// </summary>
    [Fact]
    public async Task AnActorThatNeverDrainsIsAbandonedWithinTheBound()
    {
        // Arrange
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();

        // Never completes: the wedged-send case, which the timeout is the only thing bounding.
        actor.Completion.Returns(new TaskCompletionSource().Task);

        (FakeConnection connection, Func<Task> run) = Arrange(() => Task.CompletedTask, actor);

        // Act
        Task session = run();

        // Assert
        await session.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        connection.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure, "the close is attempted regardless");
        LogRecords.Should().ContainSingle(r => r.Level == LogLevel.Warning)
                  .Which.Message.Should().Contain("abandoning");
    }

    /// <summary>
    /// A faulted actor loop is logged, not rethrown: this happens in a <c>finally</c>, where throwing
    /// would replace whatever exception sent us into it.
    /// </summary>
    [Fact]
    public async Task AFaultedActorLoopIsLoggedAndNotRethrown()
    {
        // Arrange
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.Completion.Returns(Task.FromException(new InvalidOperationException("loop broke")));

        (FakeConnection connection, Func<Task> run) =
            Arrange(() => throw new JsonException("malformed"), actor);

        // Act
        Func<Task> act = run;

        // Assert - the JsonException still wins; the actor's fault does not replace it
        await act.Should().ThrowAsync<JsonException>();
        connection.CloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
        LogRecords.Should().ContainSingle(r => r.Level == LogLevel.Error);
    }

    /// <summary>
    /// The session consumes the reader, so the session completes it - the pipelines contract the
    /// controller hands it over on. Observed from the writer's side, which is where a reader's
    /// completion becomes visible.
    /// </summary>
    [Fact]
    public async Task TheReaderIsCompleted()
    {
        // Arrange
        Pipe wire = new();
        FakeConnection connection = new(_registry);
        PrinterConnectionSession session = NewSession(new StubWebSocketHandler(() => Task.CompletedTask), DrainedActor());

        // Act
        await session.RunAsync(PrinterId, connection, wire.Reader, CancellationToken.None);

        // Assert - a write after the reader completed reports it, which is how a producer learns
        FlushResult flush = await wire.Writer.WriteAsync(new byte[] { 1 }, TestContext.Current.CancellationToken);
        flush.IsCompleted.Should().BeTrue();
    }

    /// <summary>Hands the session a caller-supplied actor instead of building one over a socket.</summary>
    private sealed class StubActorFactory(IPrinterConnectionActor actor)
        : PrinterConnectionActorFactory(Substitute.For<ITelemetrySink>(),
                                        NullLogger<PrinterConnectionActor>.Instance,
                                        Options.Create(new PrusaConnectOptions()),
                                        Substitute.For<ITransferContentStore>())
    {
        public override IPrinterConnectionActor Create(int printerId, IPrinterConnection connection)
        {
            return actor;
        }
    }

    /// <summary>Supplies the read loop's ending, which is all the session cares about.</summary>
    private sealed class StubWebSocketHandler(Func<Task> end)
        : WebSocketHandler(NullLogger<WebSocketHandler>.Instance,
            new MessageDispatcher(NullLogger<MessageDispatcher>.Instance,
                new UnknownFieldTracker(NullLogger<UnknownFieldTracker>.Instance),
                TimeProvider.System))
    {
        public override Task HandlePrusaWebsocket(PipeReader input, int printerId, IPrinterConnectionActor actor,
                                                  CancellationToken cancellationToken)
        {
            return end();
        }
    }

    /// <summary>
    /// A connection that records how it was closed, and - the point of it - whether the printer was
    /// still reachable through the registry at that moment. A substitute could assert the call
    /// order; only this can assert the state a racing command would actually have observed.
    /// </summary>
    private sealed class FakeConnection(PrinterConnectionRegistry registry) : IClosablePrinterConnection
    {
        public WebSocketCloseStatus? CloseStatus { get; private set; }

        public bool WasRegisteredAtClose { get; private set; }

        public bool IsOpen => true;

        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        // These tests are about the session's teardown ordering; nothing here sends a transfer chunk.
        public ValueTask SendChunkAsync(ReadOnlyMemory<byte> header, ITransferContent content, long offset,
            long count, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public Task CloseOutputAsync(WebSocketCloseStatus closeStatus)
        {
            CloseStatus = closeStatus;
            WasRegisteredAtClose = registry.TryGet(PrinterId, out _);

            return Task.CompletedTask;
        }
    }
}
