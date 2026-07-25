using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using PrinterService.Host.PrusaConnect.Commands;

namespace PrinterService.Host.PrusaConnect;

/// <summary>
/// The single-threaded owner of one printer's live connection - the socket write side, command-id
/// allocation, the in-flight command and its ack correlation, and (once built) the transfer state
/// machine. Everything arrives as a <see cref="ConnectionMessage"/> and is processed strictly in
/// order, so none of that state needs a lock: same shape as <see cref="TelemetryWriter"/>, per
/// notes/concurrency-model.md.
/// </summary>
public interface IPrinterConnectionActor
{
    /// <summary>Whether the underlying socket is open. Liveness for the UI, not a send guarantee.</summary>
    bool IsOpen { get; }

    /// <summary>Completes once the mailbox has been completed <b>and</b> drained - the actor's
    /// equivalent of <see cref="TelemetryWriter"/>'s shutdown-by-completion.</summary>
    Task Completion { get; }

    /// <summary>Posts an inbound message from the read loop. Waits when the mailbox is full, which
    /// deliberately stops the socket read and lets TCP push back on the printer.</summary>
    ValueTask PostAsync(ConnectionMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a command and awaits the printer's correlated reply. <paramref name="cancellationToken"/>
    /// is the caller's own (e.g. the HTTP request being aborted) and propagates as an ordinary
    /// <see cref="OperationCanceledException"/>; disconnect and timeout are the actor's business and
    /// come back as <see cref="CommandSendOutcome"/> values instead.
    /// </summary>
    Task<CommandSendResult> SendCommandAsync(ISendableCommand command, CancellationToken cancellationToken);

    /// <summary>Completes the mailbox. The loop drains what is already queued, fails any in-flight
    /// command as <see cref="CommandSendOutcome.NotConnected"/>, and exits - no cancellation token
    /// is threaded into the work at all.</summary>
    void Complete();
}

public sealed class PrinterConnectionActor : IPrinterConnectionActor
{
    /// <summary>
    /// Inbound messages arrive at ~1/s per printer (telemetry cadence), so this is minutes of slack;
    /// it exists so a stalled loop surfaces as socket backpressure rather than unbounded memory.
    /// </summary>
    private const int MailboxCapacity = 64;

    private readonly int _printerId;
    private readonly IPrinterConnection _connection;
    private readonly ITelemetrySink _sink;
    private readonly ILogger<PrinterConnectionActor> _logger;
    private readonly TimeSpan _responseTimeout;
    private readonly Channel<ConnectionMessage> _mailbox;

    // Loop-only state. No locks, no Interlocked, no ConcurrentDictionary: only the loop touches
    // these, which is the entire point of the actor.
    private Pending? _pending;
    private uint _lastCommandId;

    // The exception to that: written by whoever tears the connection down, read by the loop while
    // it drains. Volatile because those are different threads; monotonic, so there is nothing to
    // race - it only ever goes false to true.
    private volatile bool _draining;

    private sealed record Pending(uint CommandId, string WireName, TaskCompletionSource<CommandSendResult> Completion, long SentAt);

    public PrinterConnectionActor(int printerId, IPrinterConnection connection, ITelemetrySink sink,
        ILogger<PrinterConnectionActor> logger, TimeSpan responseTimeout)
    {
        _printerId = printerId;
        _connection = connection;
        _sink = sink;
        _logger = logger;
        _responseTimeout = responseTimeout;

        _mailbox = Channel.CreateBounded<ConnectionMessage>(new BoundedChannelOptions(MailboxCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        _lastCommandId = CommandIdSeed.Next();

        Completion = RunAsync();
    }

    public bool IsOpen => _connection.IsOpen;

    public Task Completion { get; }

    public ValueTask PostAsync(ConnectionMessage message, CancellationToken cancellationToken) =>
        _mailbox.Writer.WriteAsync(message, cancellationToken);

    public async Task<CommandSendResult> SendCommandAsync(ISendableCommand command, CancellationToken cancellationToken)
    {
        // RunContinuationsAsynchronously: the loop completes this while processing the printer's
        // answering event; without the flag the caller's continuation would run inline on the loop,
        // delaying the next message until it's done.
        TaskCompletionSource<CommandSendResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await _mailbox.Writer.WriteAsync(new SendCommandMessage(command, completion, cancellationToken), cancellationToken);
        }
        catch (ChannelClosedException)
        {
            // Mailbox already completed: the connection is gone (or going). The message never
            // entered the mailbox, so nothing will ever answer it - report that directly.
            return new CommandSendResult(CommandSendOutcome.NotConnected, null);
        }

        // WaitAsync binds only the caller's own token. Disconnect and timeout complete the task
        // itself, from the loop - there is nothing here to distinguish, hence no exception filters.
        return await completion.Task.WaitAsync(cancellationToken);
    }

    public void Complete()
    {
        // Set before completing the writer, so the loop can never dequeue a command without knowing
        // teardown has begun. The channel does not expose "writer completed" on its own - Reader
        // .Completion only finishes once the drain is over, which is too late to be useful here.
        _draining = true;

        _mailbox.Writer.TryComplete();
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                // Always drain what has already arrived before considering the deadline. The
                // timeout governs *waiting*, not processing: an ack sitting in the mailbox arrived
                // when it arrived, and reporting TimedOut because the loop was slow to reach it
                // would both lie to the caller and leave the ack to be persisted as an ordinary
                // unmatched event. FIFO bounds how far this can postpone the deadline - only
                // messages that genuinely arrived first can come between.
                if (!_mailbox.Reader.TryRead(out ConnectionMessage? message))
                {
                    if (_pending is null)
                    {
                        if (!await _mailbox.Reader.WaitToReadAsync())
                        {
                            break;
                        }
                    }
                    else
                    {
                        // The one place a deadline exists: while a command is in flight, wait for
                        // the next message only until the response timeout expires. The token never
                        // touches the work itself - expiry is just "stop waiting", handled in one
                        // place, by the one owner of the pending state.
                        TimeSpan remaining = _responseTimeout - Stopwatch.GetElapsedTime(_pending.SentAt);

                        if (remaining <= TimeSpan.Zero)
                        {
                            TimeOutPending();
                            continue;
                        }

                        using CancellationTokenSource deadline = new(remaining);

                        bool more;

                        try
                        {
                            more = await _mailbox.Reader.WaitToReadAsync(deadline.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            TimeOutPending();
                            continue;
                        }

                        if (!more)
                        {
                            break;
                        }
                    }

                    continue;
                }

                try
                {
                    await HandleAsync(message);
                }
                catch (Exception e)
                {
                    // A message must never kill the loop: the connection would live on with an actor
                    // that answers nothing, and every later SendCommandAsync would hang - the actor
                    // owns the response timeout, so no caller has one of their own to fall back on.
                    if (message is SendCommandMessage send)
                    {
                        Fail(send.Completion, e);
                    }
                    else
                    {
                        _logger.LogError(e, "[{PrinterId}] unhandled error processing {MessageType}", _printerId, message.GetType().Name);
                    }
                }
            }
        }
        finally
        {
            // Mailbox completed and drained: the connection is gone. A command still awaiting its
            // reply will never get one - fail it now rather than making the caller wait out the
            // response timeout. This line is what replaced the controller-finally-plus-exception-
            // filter dance (notes/concurrency-model.md: "a line in a switch").
            _pending?.Completion.TrySetResult(new CommandSendResult(CommandSendOutcome.NotConnected, null));
            _pending = null;
        }
    }

    private async Task HandleAsync(ConnectionMessage message)
    {
        switch (message)
        {
            case SendCommandMessage send:
                await HandleSendAsync(send);
                break;

            case InboundEventMessage inboundEvent:
                HandleEvent(inboundEvent);
                break;

            case InboundTelemetryMessage telemetry:
                _sink.Enqueue(_printerId, telemetry.ReceivedAt, telemetry.Telemetry);
                break;

            case InboundTransferRequestMessage:
                // Serving chunks back is the transfer feature (notes/transfer-protocol.md), not yet
                // built. Recognized-but-unserved, same as before the actor existed.
                _logger.LogDebug("[{PrinterId}] inline transfer chunk request (not yet served)", _printerId);
                break;
        }
    }

    private async Task HandleSendAsync(SendCommandMessage send)
    {
        // Checked before anything else, because everything else has a cost the caller is no longer
        // there to receive: writing to the printer, and taking the one in-flight slot until the
        // answer or the timeout. Posting and executing being separate steps is what makes this
        // possible at all - cancelling the caller ends its wait without touching the queued message.
        //
        // The window this closes is the whole queueing delay. What it cannot close is the few
        // instructions between here and the write completing, and that is deliberate: the send takes
        // CancellationToken.None because letting one caller's cancellation abort a write mid-frame
        // would corrupt the stream for every other user of this connection.
        if (send.CallerToken.IsCancellationRequested)
        {
            send.Completion.TrySetCanceled(send.CallerToken);

            return;
        }

        if (_draining)
        {
            // Teardown has started and this was queued before anyone knew. Sending now would be
            // worse than useless: the loop's finally reports whatever it leaves pending as
            // NotConnected, so the caller would be told the printer was unreachable by a command
            // that had in fact just executed. On the clean-shutdown path the socket is still open at
            // this point - IsOpen below would not catch it - so the frame really would arrive.
            send.Completion.TrySetResult(new CommandSendResult(CommandSendOutcome.NotConnected, null));

            return;
        }

        if (_pending is not null)
        {
            // One in-flight command per printer, matching the firmware's own limit
            // (connect.cpp:469-476 at the pinned ref). Enforced by an ordinary null check on
            // loop-only state - this used to be a ConcurrentDictionary.TryAdd acting as a mutex.
            send.Completion.TrySetResult(new CommandSendResult(CommandSendOutcome.AlreadyInFlight, null));

            return;
        }

        if (!_connection.IsOpen)
        {
            send.Completion.TrySetResult(new CommandSendResult(CommandSendOutcome.NotConnected, null));

            return;
        }

        uint commandId = unchecked(++_lastCommandId);
        byte[] frame = CommandWireEncoder.Encode(commandId, send.Command);

        try
        {
            await _connection.SendAsync(frame, CancellationToken.None);
        }
        catch (Exception e)
        {
            // Never reached the printer, so nothing is pending - the caller gets the real error and
            // the next command can begin immediately.
            Fail(send.Completion, e);

            return;
        }

        _pending = new Pending(commandId, send.Command.WireName, send.Completion, Stopwatch.GetTimestamp());
    }

    private void HandleEvent(InboundEventMessage message)
    {
        DTO.EventMessages.EventDTO eventDto = message.Event;

        if (_pending is not null && eventDto.CommandId == _pending.CommandId)
        {
            Pending answered = _pending;

            _pending = null;
            answered.Completion.TrySetResult(new CommandSendResult(CommandSendOutcome.Completed,
                new CommandOutcome(eventDto.EventType, eventDto.Reason)));
        }

        // Answering a command doesn't consume the event - it is still an ordinary event
        // (Finished/Rejected/StateChanged) and is persisted like any other.
        _sink.Enqueue(_printerId, message.ReceivedAt, eventDto);
    }

    /// <summary>
    /// Faults a caller's completion, and marks the fault observed.
    /// </summary>
    /// <remarks>
    /// The second half matters because the caller may already be gone: once its
    /// <c>WaitAsync(token)</c> has lost to cancellation, nothing ever awaits this task, and .NET
    /// raises <see cref="TaskScheduler.UnobservedTaskException"/> when it is finalised - confirmed
    /// on .NET 10 rather than assumed. Teardown makes that the common case rather than a curiosity:
    /// abandoning a wedged actor disposes the socket, which faults the outstanding send exactly when
    /// no caller remains. Default runtime behaviour is only noise, but any host configured to
    /// escalate unobserved exceptions would fail on it.
    ///
    /// Reading <c>Exception</c> is what marks it observed; a caller still waiting receives it
    /// unchanged.
    /// </remarks>
    private static void Fail(TaskCompletionSource<CommandSendResult> completion, Exception error)
    {
        if (completion.TrySetException(error))
        {
            _ = completion.Task.Exception;
        }
    }

    private void TimeOutPending()
    {
        Pending expired = _pending!;

        _pending = null;
        _logger.LogWarning("[{PrinterId}] command {CommandId} ({Command}) timed out waiting for a reply",
            _printerId, expired.CommandId, expired.WireName);
        expired.Completion.TrySetResult(new CommandSendResult(CommandSendOutcome.TimedOut, null));
    }
}
