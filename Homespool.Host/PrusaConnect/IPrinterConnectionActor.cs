using System;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.PrusaConnect.Commands;

namespace Homespool.Host.PrusaConnect;

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
