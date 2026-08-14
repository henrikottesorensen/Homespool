using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.PrusaConnect.DTO.Transfers;
using Homespool.Host.PrusaConnect.Transfers;
using Homespool.Host.Services;
using Homespool.Host.Telemetry;

namespace Homespool.Host.PrusaConnect;

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
    private readonly ITransferContentStore _contentStore;
    private readonly ILogger<PrinterConnectionActor> _logger;
    private readonly TimeSpan _responseTimeout;
    private readonly Channel<ConnectionMessage> _mailbox;

    // Per-message faults are logged through a throttle: no known message type throws today, but the
    // catch-all guards against exactly the unforeseen, and if a crafted message ever finds a way to
    // throw, an attacker could drive this Error-with-stack at wire rate. Per-connection, so the
    // printer id in the summary stays meaningful. See LogThrottle's remarks for the numbers.
    private readonly LogThrottle _faultWarnings = new(TimeSpan.FromSeconds(10));

    // Loop-only state. No locks, no Interlocked, no ConcurrentDictionary: only the loop touches
    // these, which is the entire point of the actor.
    private Pending? _pending;
    private uint _lastCommandId;

    /// <summary>
    /// The one transfer this printer may have in progress. One, because firmware allocates a single
    /// system-wide transfer slot (<c>Monitor</c>, monitor.hpp:85-98) and requests are strictly
    /// sequential - so this is a field rather than a collection, and "no interleaving" is a property
    /// of the type rather than something to enforce.
    /// </summary>
    private ActiveTransfer? _transfer;

    // The exception to that: written by whoever tears the connection down, read by the loop while
    // it drains. Volatile because those are different threads; monotonic, so there is nothing to
    // race - it only ever goes false to true.
    private volatile bool _draining;

    private sealed record Pending(uint CommandId, string WireName, TaskCompletionSource<CommandSendResult> Completion, long SentAt);

    /// <summary>
    /// A transfer the printer is currently pulling from us.
    /// </summary>
    /// <param name="Hash">Our token from <c>START_CONNECT_DOWNLOAD</c> - what the first request
    /// quotes back, and the only link between an offer and the requests it provokes.</param>
    /// <param name="Content">The bytes, owned by this record and disposed when the transfer ends.</param>
    /// <param name="FileId">The printer's own nonce, learned from that first request and echoed in
    /// every chunk header thereafter. Changes on a <c>RangeJump</c>, which renegotiates.</param>
    /// <param name="TransferId">
    /// The printer's transfer id, which the first request carries and which its terminal
    /// <c>TRANSFER_*</c> events carry at the root - so it, not <see cref="FileId"/>, is what matches
    /// an ending to the transfer that ended. Stable across a <c>RangeJump</c>: the renegotiation
    /// resends the stored id rather than allocating one (<c>restart_download</c>,
    /// transfer.cpp:161-223). Null only if a printer omitted it, which firmware never does.
    /// </param>
    private sealed record ActiveTransfer(string Hash, ITransferContent Content, uint FileId, int? TransferId);

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

    /// <summary>
    /// How long to wait for a frame to finish being written to the socket before giving up on the
    /// connection entirely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This bounds the <i>wait</i>, not the send: the send keeps <see cref="CancellationToken.None"/>
    /// because cancelling <c>WebSocket.SendAsync</c> mid-frame leaves a partial message on the wire
    /// and aborts the socket, which is strictly worse than waiting. Abandoning the wait leaves the
    /// write to be resolved by the teardown that follows - disposing the socket faults it in
    /// milliseconds (measured).
    /// </para>
    /// <para>
    /// Generous, because it must never fire for a merely slow-but-progressing write. A ~40-byte
    /// command frame fits the send buffer and completes instantly, which is why nothing has ever hit
    /// this; a 256 KiB transfer chunk will legitimately await real network progress. It exists for
    /// the case that has no other bound at all: a peer that is alive but has stopped draining its
    /// socket, where the write never completes and, because this loop is also the telemetry path,
    /// the printer goes silent with nothing logged.
    /// </para>
    /// <para>
    /// Not configuration, for the same reason as
    /// <see cref="PrinterConnectionSession.ActorDrainTimeout"/>: the symptom that would prompt tuning
    /// is a wedged peer, which no value fixes. Revisit if transfers show real chunk writes coming
    /// anywhere near it.
    /// </para>
    /// </remarks>
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public PrinterConnectionActor(int printerId,
                                  IPrinterConnection connection,
                                  ITelemetrySink sink,
                                  ILogger<PrinterConnectionActor> logger,
                                  TimeSpan responseTimeout,
                                  ITransferContentStore contentStore)
    {
        _printerId = printerId;
        _connection = connection;
        _sink = sink;
        _logger = logger;
        _responseTimeout = responseTimeout;
        _contentStore = contentStore;

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

    public ValueTask PostAsync(ConnectionMessage message, CancellationToken cancellationToken)
    {
        return _mailbox.Writer.WriteAsync(message, cancellationToken);
    }

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
                    else if (_faultWarnings.Record() is { } window)
                    {
                        if (window.IsFirstOccurrence)
                        {
                            _logger.LogError(e, "unhandled error processing {MessageType}", message.GetType().Name);
                        }
                        else
                        {
                            _logger.LogError(e,
                                             "unhandled error processing {MessageType} - {Count} such fault(s) in the last {ElapsedSeconds:F0}s, {Total} since this connection opened. The latest fault's exception is attached.",
                                             message.GetType().Name, window.Count, window.Elapsed.TotalSeconds, window.Total);
                        }
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

            // A transfer in progress dies with the connection - there is no way to serve the rest,
            // and the printer will start over on reconnect with a fresh file_id. Disposing here is
            // what keeps a dropped connection mid-transfer from leaking the open file, and this is
            // the one place every connection's life ends.
            _transfer?.Content.Dispose();
            _transfer = null;
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
                EnqueueTelemetry(telemetry);
                break;

            case InboundTransferRequestMessage transferRequest:
                await HandleTransferRequestAsync(transferRequest.Request);
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
            // WaitAsync bounds the wait, never the write. The token stays None deliberately (see the
            // comment above); giving up on the wait leaves the send running, which is safe precisely
            // because the teardown this triggers disposes the socket and faults it.
            await _connection.SendAsync(frame, CancellationToken.None).AsTask().WaitAsync(SendTimeout);
        }
        catch (TimeoutException)
        {
            // The one stall nothing else bounds. The response timeout cannot help - it does not start
            // until this write returns - and the read loop is by now parked in PostAsync against a
            // full mailbox, so it will not notice the peer either. Left alone this leaks the request,
            // the socket and this actor, permanently and silently, while the printer's own 60s socket
            // timeout lets *it* reconnect and look healthy.
            //
            // So: give up on the connection rather than on the command. Completing the mailbox makes
            // the read loop's next PostAsync its ordinary exit, which unwinds to the session's
            // teardown, which disposes the socket, which faults the abandoned write.
            _logger.LogError(
                "writing {Command} did not complete within {SendTimeout}; abandoning the connection.",
                send.Command.WireName, SendTimeout);

            Fail(send.Completion, new Exceptions.CommandSendTimedOutException(_printerId));

            _draining = true;
            _mailbox.Writer.TryComplete();

            return;
        }
        catch (Exception e)
        {
            // Never reached the printer, so nothing is pending - the caller gets the real error and
            // the next command can begin immediately.
            Fail(send.Completion, e);

            return;
        }

        // The wire name and the id, never the frame or the command's arguments. SetToken exists as a
        // command class already, and duplicate-connection-identity.md makes SET_TOKEN the answer to a
        // compromised printer credential - so a payload-logging habit here would turn that fix into a
        // credential written to disk. Same rule as UnknownFieldTracker's.
        _logger.LogDebug("sent {Command} as command {CommandId}", send.Command.WireName, commandId);

        if (!send.Command.ExpectsReply)
        {
            // Never takes the in-flight slot, because nothing would ever free it: the printer cannot
            // answer this command (RESET_PRINTER reboots instead of replying). Holding the slot would
            // block every later command until the response timeout expired, and then report failure
            // for a command that succeeded.
            //
            // So this one logs "sent" and never logs an answer. That is the protocol, not a lost ack.
            send.Completion.TrySetResult(new CommandSendResult(CommandSendOutcome.Dispatched, null));

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

            // The other half of the pair above, and the reason SentAt is on Pending at all: elapsed
            // is what separates a sluggish printer from a wedged one, and neither is visible from
            // the outcome alone. Reason is firmware's own rejection text (JC's macro strings), not
            // anything we compose - safe to log, and usually the only explanation of a Rejected.
            _logger.LogDebug(
                "command {CommandId} ({Command}) answered with {EventType} after {ElapsedMs:F0}ms{Reason} [{MachineReason}]",
                answered.CommandId,
                answered.WireName,
                eventDto.EventType,
                Stopwatch.GetElapsedTime(answered.SentAt).TotalMilliseconds,
                eventDto.Reason is null ? string.Empty : $": {eventDto.Reason}",
                eventDto.MachineReason);

            // Data rides on the result rather than the outcome, and never reaches the log line above:
            // a payload is the one part of an answer that can carry anything, and FILE_INFO's runs to
            // ~90 KB with a preview in it. PrinterCommandService is the only thing that reads it.
            answered.Completion.TrySetResult(new CommandSendResult(CommandSendOutcome.Completed,
                                                                   new CommandOutcome(eventDto.EventType, eventDto.Reason)
                                                                   {
                                                                       MachineReason = eventDto.MachineReason,
                                                                   },
                                                                   eventDto.Data));
        }

        EndTransferIfTerminal(eventDto);

        // Answering a command doesn't consume the event - it is still an ordinary event
        // (Finished/Rejected/StateChanged) and is persisted like any other. Mapping happens here,
        // at the last point that knows this connection speaks Prusa Connect; an unmapped wire
        // value's throw is the loop's throttled catch-all's business, costing one message and one
        // aggregated log line, never the connection. The ack correlation above already ran, so a
        // command's caller is unaffected either way.
        _sink.Enqueue(_printerId, message.ReceivedAt, PrusaTelemetryMapping.ToRecord(eventDto, message.Identity));
    }

    /// <summary>
    /// Converts at the edge and hands the sink the neutral currency - the last point that knows
    /// this connection speaks Prusa Connect. Deliberately no try/catch of its own: an unmapped
    /// wire state (ParseWireState's loud throw, <c>notes/protocol-vocabulary-boundary.md</c>) is
    /// handled by the loop's throttled catch-all, which drops the one message and aggregates the
    /// logging - a local catch here would log unthrottled at wire rate, the exact flood
    /// <see cref="Services.LogThrottle"/> exists to prevent.
    /// </summary>
    private void EnqueueTelemetry(InboundTelemetryMessage telemetry)
    {
        _sink.Enqueue(_printerId, telemetry.ReceivedAt, PrusaTelemetryMapping.ToUpdate(telemetry.Telemetry));
    }

    /// <summary>
    /// Answers one range request with the bytes it asked for - or, when it cannot be answered, ends
    /// the transfer deliberately rather than leaving it hanging.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Serve the whole range or fail it.</b> Firmware's inline engine carries no timestamp
    /// (<c>Download::Inline</c>, download.hpp:138-151) and therefore no stall timeout: a short answer
    /// leaves the printer waiting forever, mid-print, with nothing to break the deadlock. A
    /// zero-length chunk is its defined "error indicated by server" signal (download.cpp:556-577),
    /// which is the one way to end a transfer promptly - so this sends the header alone when it must
    /// give up, and never as a side effect of anything else.
    /// </para>
    /// <para>
    /// Requests are strictly sequential (download.cpp:526-554), so there is never more than one of
    /// these in flight for a printer, and no queue to manage.
    /// </para>
    /// </remarks>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                     Justification = "Ownership moves to _transfer; disposed by the loop's finally or by FailTransferAsync.")]
    private async Task HandleTransferRequestAsync(InlineRequestDTO request)
    {
        // A hash means "this is the first request of a negotiation" - firmware sends the details
        // block once per negotiation and omits it thereafter (render.cpp:104-108). Note *per
        // negotiation*, not per transfer: a RangeJump restarts the download with a fresh random
        // file_id and re-sends the hash (transfer.cpp:161-223), and plain gcode over 512 KiB always
        // does one, because it fetches the last 50 000 bytes first so GcodeInfo can scan the preview
        // and only then jumps to the body (transfer.cpp:34-49, :225-236). So a hash-bearing request
        // must always (re)bind the file_id - treating a familiar hash with an unfamiliar file_id as
        // a foreign transfer would ignore the jump, and the printer would wait forever for a chunk
        // that never comes.
        if (request.Hash is not null)
        {
            if (_transfer is not null && _transfer.Hash == request.Hash)
            {
                // Same content, renegotiated. Keep the open handle and adopt the new id.
                _transfer = _transfer with { FileId = request.FileId };
            }
            else if (_contentStore.TryOpen(request.Hash, out ITransferContent? content))
            {
                _transfer?.Content.Dispose();

                // Ownership moves to _transfer, which the loop's finally disposes when the
                // connection ends, and FailTransferAsync disposes when the transfer ends early.
                // CA2000 cannot see across those, hence the suppression rather than a using.
                _transfer = new ActiveTransfer(request.Hash, content, request.FileId, request.TransferId);
            }
            else
            {
                // Ordinary rather than alarming: a printer resuming a transfer we have forgotten
                // across a restart looks exactly like this.
                _logger.LogInformation("transfer request for unknown hash, failing it");
                await FailTransferAsync(request.FileId);

                return;
            }
        }

        if (_transfer is null)
        {
            // No hash and no active transfer: a stray request, most likely for a transfer that ended
            // while this was in flight. Firmware blackholes our stray chunks the same way
            // (planner.cpp:1191-1200), so failing it is the symmetric, harmless answer.
            _logger.LogDebug("transfer request with no active transfer");
            await FailTransferAsync(request.FileId);

            return;
        }

        if (request.FileId != _transfer.FileId)
        {
            // A different transfer's request. Answering it with this transfer's bytes is precisely
            // what firmware's file_id check exists to catch, and would kill a live print.
            _logger.LogWarning("transfer request for file_id {Requested}, active is {Active}",
                               request.FileId, _transfer.FileId);

            return;
        }

        long count = request.End - request.Start + 1; // End is inclusive (download.cpp:489).

        if (count <= 0 || request.Start < 0 || request.End >= _transfer.Content.Length)
        {
            _logger.LogWarning("transfer request {Start}..{End} outside the {Length}-byte file",
                               request.Start, request.End, _transfer.Content.Length);
            await FailTransferAsync(request.FileId);

            return;
        }

        byte[] header = ChunkWireEncoder.EncodeHeader(request.FileId);

        try
        {
            // Same discipline as HandleSendAsync: bound the wait, never the write. A chunk send is
            // reads and writes interleaved, and neither has any other bound - the reads because a
            // data directory can be a network mount, the writes because a peer can stop draining.
            await _connection.SendChunkAsync(header, _transfer.Content, request.Start, count, CancellationToken.None)
                             .AsTask()
                             .WaitAsync(SendTimeout);
        }
        catch (TimeoutException)
        {
            // Give up on the connection, not the chunk - the same conclusion HandleSendAsync reaches,
            // and for the same reason: nothing else can break a stalled write, and the printer's own
            // 60s socket timeout lets it reconnect while our side would leak silently.
            _logger.LogError("transfer chunk write did not finish within {Timeout}, abandoning the connection",
                             SendTimeout);
            Complete();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The read failed or the socket went away mid-chunk. Tell the printer, so a print that
            // will never complete stops waiting for us.
            _logger.LogWarning(e, "transfer chunk failed");
            await FailTransferAsync(request.FileId);
        }
    }

    /// <summary>
    /// Releases the transfer's content when the printer says the transfer has ended - successfully or
    /// otherwise. Without this, a completed transfer's file handle would stay open until the
    /// connection itself ended, which for a printer that stays connected for weeks means indefinitely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matched on <c>transfer_id</c>, which the terminal events carry at the root and which the first
    /// range request gave us. Not on <c>file_id</c>, which a <c>RangeJump</c> changes, and not on
    /// "any terminal event": the printer's transfer slot is shared with PrusaLink uploads
    /// (<c>Monitor::Type</c>, monitor.hpp:85-98), and an event ending one of those must not release a
    /// download of ours. Only one transfer of any kind runs at a time, so in practice these coincide -
    /// matching on the id costs one comparison and removes the need to rely on that.
    /// </para>
    /// <para>
    /// Nothing is sent here. These events are unsolicited reports that it is already over; a chunk or
    /// a failure signal now would be answering a question nobody asked.
    /// </para>
    /// </remarks>
    private void EndTransferIfTerminal(DTO.EventMessages.EventDTO eventDto)
    {
        if (_transfer is null)
        {
            return;
        }

        if (eventDto.EventType is not (Model.PrinterEventType.TransferFinished or Model.PrinterEventType.TransferAborted
            or Model.PrinterEventType.TransferStopped))
        {
            return;
        }

        if (_transfer.TransferId is not null && eventDto.TransferId != _transfer.TransferId)
        {
            _logger.LogDebug("{EventType} for transfer {Reported}, ours is {Ours} - leaving it open",
                             eventDto.EventType, eventDto.TransferId, _transfer.TransferId);

            return;
        }

        _logger.LogDebug("transfer ended with {EventType}", eventDto.EventType);
        _transfer.Content.Dispose();
        _transfer = null;
    }

    /// <summary>
    /// Ends a transfer the deliberate way: a chunk with a header and no payload, which firmware reads
    /// as "error indicated by server" (download.cpp:556-577).
    /// </summary>
    /// <remarks>
    /// The only place allowed to send an empty chunk. Everywhere else it would be a silent
    /// print-killer, which is why the zero-length case is not reachable from the normal send path.
    /// </remarks>
    private async Task FailTransferAsync(uint fileId)
    {
        _transfer?.Content.Dispose();
        _transfer = null;

        try
        {
            await _connection.SendAsync(ChunkWireEncoder.EncodeHeader(fileId), CancellationToken.None)
                             .AsTask()
                             .WaitAsync(SendTimeout);
        }
        catch (Exception e) when (e is TimeoutException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Best effort - the connection is already in trouble if this fails, and the printer will
            // notice that separately.
            _logger.LogDebug(e, "could not signal transfer failure");
        }
    }

    private void TimeOutPending()
    {
        Pending expired = _pending!;

        _pending = null;
        _logger.LogWarning("command {CommandId} ({Command}) timed out waiting for a reply",
                           expired.CommandId, expired.WireName);
        expired.Completion.TrySetResult(new CommandSendResult(CommandSendOutcome.ResponseTimedOut, null));
    }
}
