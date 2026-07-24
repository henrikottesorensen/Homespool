using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using PrinterService.Host.PrusaConnect;
using PrinterService.Host.PrusaConnect.Commands;
using PrinterService.Model;

namespace PrinterService.Host.Test;

/// <summary>
/// <see cref="PrinterCommandTransport"/> - not-connected/already-in-flight/timeout handling, and
/// that a send which never reaches the printer doesn't leave the correlator wedged.
/// </summary>
public class PrinterCommandTransportTests
{
    private sealed class FakeConnection : IPrinterConnection
    {
        public bool IsOpen { get; set; } = true;
        public List<byte[]> SentFrames { get; } = [];
        public Exception? ThrowOnSend { get; set; }

        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
        {
            if (ThrowOnSend is not null)
            {
                throw ThrowOnSend;
            }

            SentFrames.Add(frame.ToArray());

            return ValueTask.CompletedTask;
        }
    }

    private static PrinterCommandTransport NewTransport(PrinterConnectionRegistry registry, IPrinterCommandCorrelator correlator, TimeSpan? responseTimeout = null) =>
        new(registry, correlator, NullLogger<PrinterCommandTransport>.Instance, responseTimeout ?? TimeSpan.FromSeconds(10));

    [Fact]
    public async Task SendAsyncReturnsNotConnectedWhenThePrinterHasNoLiveConnection()
    {
        // Arrange
        PrinterConnectionRegistry registry = new();
        PrinterCommandTransport transport = NewTransport(registry, new PrinterCommandCorrelator());

        // Act
        CommandSendResult result = await transport.SendAsync(1, new PausePrint(), CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(CommandSendOutcome.NotConnected);
    }

    [Fact]
    public async Task SendAsyncCompletesWhenTheCorrelatorObservesTheMatchingEvent()
    {
        // Arrange
        PrinterConnectionRegistry registry = new();
        FakeConnection connection = new();
        registry.Register(1, connection);

        PrinterCommandCorrelator correlator = new();
        PrinterCommandTransport transport = NewTransport(registry, correlator);

        Task<CommandSendResult> sendTask = transport.SendAsync(1, new PausePrint(), CancellationToken.None);

        // The transport assigns the CommandId internally; the frame it wrote to the connection is
        // the only place to read it back from (bytes 1-8, 8 hex digits).
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        uint commandId = uint.Parse(System.Text.Encoding.ASCII.GetString(connection.SentFrames[0], 1, 8), System.Globalization.NumberStyles.HexNumber);

        // Act
        correlator.ObserveEvent(1, commandId, Events.Finished, null);
        CommandSendResult result = await sendTask;

        // Assert
        result.Outcome.Should().Be(CommandSendOutcome.Completed);
        result.Response!.EventType.Should().Be(Events.Finished);
        connection.SentFrames.Should().ContainSingle();
    }

    [Fact]
    public async Task SecondConcurrentSendWhileOneIsPendingReturnsAlreadyInFlight()
    {
        // Arrange
        PrinterConnectionRegistry registry = new();
        FakeConnection connection = new();
        registry.Register(1, connection);

        PrinterCommandCorrelator correlator = new();
        PrinterCommandTransport transport = NewTransport(registry, correlator);

        Task<CommandSendResult> firstSend = transport.SendAsync(1, new PausePrint(), CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        // Act
        CommandSendResult secondResult = await transport.SendAsync(1, new ResumePrint(), CancellationToken.None);

        // Assert
        secondResult.Outcome.Should().Be(CommandSendOutcome.AlreadyInFlight);
        connection.SentFrames.Should().ContainSingle("the rejected second send must never reach the wire");

        // Cleanup: let the first send's wait finish so the test process doesn't leak a pending task.
        correlator.Cancel(1);
        await firstSend;
    }

    /// <summary>
    /// Mirrors what happens when the printer disconnects while a command is still awaiting a reply:
    /// PrusaConnectPrinterController's finally block calls IPrinterCommandCorrelator.Cancel directly,
    /// not via the response timeout. Before this was handled explicitly, the resulting
    /// OperationCanceledException wasn't bound to timeoutCts and propagated unhandled - all the way
    /// up through PrinterCommandService to the Razor Page, past every typed catch clause there, as an
    /// unhandled 500 rather than a message.
    /// </summary>
    [Fact]
    public async Task SendAsyncReturnsNotConnectedWhenTheCorrelatorIsCancelledDirectlyWhileWaiting()
    {
        // Arrange
        PrinterConnectionRegistry registry = new();
        FakeConnection connection = new();
        registry.Register(1, connection);

        PrinterCommandCorrelator correlator = new();
        // Long timeout so a genuine timeout can't race the direct cancellation below and mask it.
        PrinterCommandTransport transport = NewTransport(registry, correlator, TimeSpan.FromSeconds(30));

        Task<CommandSendResult> sendTask = transport.SendAsync(1, new PausePrint(), CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        // Act
        // Simulates the controller's disconnect handling - cancelling the correlator directly,
        // not through the transport's own timeout.
        correlator.Cancel(1);
        CommandSendResult result = await sendTask;

        // Assert
        result.Outcome.Should().Be(CommandSendOutcome.NotConnected);

        // Not wedged: a new send for the same printer can begin immediately.
        bool began = correlator.TryBeginCommand(1, 789, out _);
        began.Should().BeTrue();
    }

    /// <summary>
    /// The caller's own token (e.g. the HTTP request itself being aborted) is a different case from
    /// the one above and must not be swallowed into a CommandSendResult nobody will ever read -
    /// it propagates as an ordinary OperationCanceledException, which ASP.NET Core already handles.
    /// </summary>
    [Fact]
    public async Task SendAsyncPropagatesCancellationFromTheCallersOwnToken()
    {
        // Arrange
        PrinterConnectionRegistry registry = new();
        FakeConnection connection = new();
        registry.Register(1, connection);

        PrinterCommandCorrelator correlator = new();
        PrinterCommandTransport transport = NewTransport(registry, correlator, TimeSpan.FromSeconds(30));

        using CancellationTokenSource cts = new();

        // Act
        Task<CommandSendResult> sendTask = transport.SendAsync(1, new PausePrint(), cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        await cts.CancelAsync();

        // Assert
        await Assert.ThrowsAsync<TaskCanceledException>(() => sendTask);
    }

    [Fact]
    public async Task SendAsyncPropagatesAndDoesNotWedgeTheCorrelatorWhenTheConnectionThrows()
    {
        // Arrange
        PrinterConnectionRegistry registry = new();
        FakeConnection connection = new() { ThrowOnSend = new InvalidOperationException("socket gone") };
        registry.Register(1, connection);

        PrinterCommandCorrelator correlator = new();
        PrinterCommandTransport transport = NewTransport(registry, correlator);

        // Act
        Func<Task> act = () => transport.SendAsync(1, new PausePrint(), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Not wedged: a new send for the same printer can begin immediately.
        bool began = correlator.TryBeginCommand(1, 123, out _);
        began.Should().BeTrue();
    }

    [Fact]
    public async Task SendAsyncReturnsTimedOutWhenNoEventArrivesInTime()
    {
        // Arrange
        PrinterConnectionRegistry registry = new();
        FakeConnection connection = new();
        registry.Register(1, connection);

        PrinterCommandCorrelator correlator = new();
        PrinterCommandTransport transport = NewTransport(registry, correlator, TimeSpan.FromMilliseconds(50));

        // Act
        CommandSendResult result = await transport.SendAsync(1, new PausePrint(), CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(CommandSendOutcome.TimedOut);

        // Not wedged: a new send for the same printer can begin immediately.
        bool began = correlator.TryBeginCommand(1, 456, out _);
        began.Should().BeTrue();
    }
}
