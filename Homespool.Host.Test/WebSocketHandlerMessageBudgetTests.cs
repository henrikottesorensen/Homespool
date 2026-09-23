using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

/// <summary>
/// <c>WebSocketHandler</c> spends a <see cref="MessageBudget"/> per message: past the burst it
/// waits before reading on, it says so once per printer, and a shutdown does not wait for it.
/// </summary>
/// <remarks>
/// On a <see cref="FakeTimeProvider"/>, so a wait holds until the test moves the clock. Every
/// message is written up front, which is what a sender over its budget looks like: the bytes are
/// there, and whether they are handled is the handler's choice.
/// </remarks>
public class WebSocketHandlerMessageBudgetTests
{
    private const string Telemetry = """{"state":"PRINTING"}""";

    private readonly FakeTimeProvider _clock = new();
    private readonly FakeLogger<PrinterWireComplaints> _complaintLog = new();
    private readonly CountingMessageDispatcher _dispatcher = new();

    [Fact]
    public async Task PastTheBurstEachMessageWaitsForTheRefill()
    {
        // Arrange
        Pipe wire = new();
        Task run = NewHandler(perSecond: 1, burst: 2).HandlePrusaWebsocket(
            wire.Reader, printerId: 7, Substitute.For<IPrinterConnectionActor>(), CancellationToken.None);

        await wire.Writer.WriteAsync(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(Telemetry, 4))),
                                     TestContext.Current.CancellationToken);

        // Act, and assert as the clock moves
        await WaitUntilAsync(() => _dispatcher.Count == 2);

        // Longer than the one-second wait itself, so a wait timed on the real clock rather than the
        // handler's would have ended by now.
        await Task.Delay(TimeSpan.FromMilliseconds(1200), TestContext.Current.CancellationToken);
        _dispatcher.Count.Should().Be(2, "the burst is spent and the clock has not moved");

        _clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => _dispatcher.Count == 3);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => _dispatcher.Count == 4);

        await wire.Writer.CompleteAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        _dispatcher.Count.Should().Be(4, "a message over the budget is delayed, never dropped");
    }

    [Fact]
    public async Task AMessageOverTheBudgetIsSaidOnceNamingThePrinterAndTheSettings()
    {
        // Arrange
        Pipe wire = new();
        Task run = NewHandler(perSecond: 1, burst: 1).HandlePrusaWebsocket(
            wire.Reader, printerId: 7, Substitute.For<IPrinterConnectionActor>(), CancellationToken.None);

        // Act
        await wire.Writer.WriteAsync(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(Telemetry, 3))),
                                     TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => _dispatcher.Count == 1);

        for (int i = 0; i < 2; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await WaitUntilAsync(() => _dispatcher.Count == i + 2);
        }

        await wire.Writer.CompleteAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        FakeLogRecord line = _complaintLog.Collector.GetSnapshot().Should().ContainSingle().Subject;

        line.Level.Should().Be(LogLevel.Warning);
        Property(line, "PrinterId").Should().Be("7");
        Property(line, "Complaint").Should().Be("sent messages faster than its budget, and is being read more slowly");
        Property(line, "Detail").Should().Contain("1000 ms").And.Contain("PrusaConnect:MessagesPerSecond");
    }

    [Fact]
    public async Task AMessageWithinTheBudgetSaysNothing()
    {
        // Arrange
        Pipe wire = new();
        Task run = NewHandler(perSecond: 1, burst: 3).HandlePrusaWebsocket(
            wire.Reader, printerId: 7, Substitute.For<IPrinterConnectionActor>(), CancellationToken.None);

        // Act
        await wire.Writer.WriteAsync(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(Telemetry, 3))),
                                     TestContext.Current.CancellationToken);
        await wire.Writer.CompleteAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        _dispatcher.Count.Should().Be(3);
        _complaintLog.Collector.GetSnapshot().Should().BeEmpty();
    }

    /// <summary>
    /// A shutdown cancels the read loop, and a loop waiting out its budget must end on it at once -
    /// not after the wait, which an attacker's debt can make as long as they like.
    /// </summary>
    [Fact]
    public async Task CancellingWhileWaitingOutTheBudgetEndsTheLoopAtOnce()
    {
        // Arrange
        Pipe wire = new();
        using CancellationTokenSource connectionEnd = new();
        Task run = NewHandler(perSecond: 1, burst: 1).HandlePrusaWebsocket(
            wire.Reader, printerId: 7, Substitute.For<IPrinterConnectionActor>(), connectionEnd.Token);

        await wire.Writer.WriteAsync(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(Telemetry, 100))),
                                     TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => _dispatcher.Count == 1);

        // Act
        await connectionEnd.CancelAsync();

        // Assert
        Func<Task> ending = () => run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await ending.Should().ThrowAsync<OperationCanceledException>();
        _dispatcher.Count.Should().Be(1);
    }

    private static string? Property(FakeLogRecord record, string name)
    {
        return record.StructuredState!.FirstOrDefault(pair => pair.Key == name).Value;
    }

    /// <summary>The handler runs on its own; poll for what it has done rather than sleep a guess.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue("the read loop should have got this far by now");
    }

    private WebSocketHandler NewHandler(int perSecond, int burst)
    {
        return new(NullLogger<WebSocketHandler>.Instance,
                   _dispatcher,
                   TestOptions.Monitor(new PrusaConnectOptions { MessagesPerSecond = perSecond, MessageBurst = burst }),
                   new PrinterWireComplaints(_complaintLog),
                   _clock);
    }

    /// <summary>
    /// Counts what reaches the dispatcher and posts nothing, so the actor can stay a bare substitute.
    /// </summary>
    private sealed class CountingMessageDispatcher()
        : MessageDispatcher(NullLogger<MessageDispatcher>.Instance,
                            new UnknownFieldTracker(NullLogger<UnknownFieldTracker>.Instance),
                            TimeProvider.System,
                            PrinterTrafficLogTests.Off,
                            new PrinterWireComplaints(NullLogger<PrinterWireComplaints>.Instance))
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public override ConnectionMessage? Classify(int printerId, JsonElement root, IReadOnlyList<NonFiniteToken> nonFinite)
        {
            Interlocked.Increment(ref _count);

            return null;
        }
    }
}
