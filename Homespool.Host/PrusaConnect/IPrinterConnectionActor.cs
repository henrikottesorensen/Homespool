using System;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect.Commands;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// The single-threaded owner of one printer's live connection - the socket write side, command-id
/// allocation, the in-flight command and its ack correlation, and (once built) the transfer state
/// machine. Everything arrives as a <see cref="ConnectionMessage"/> and is processed strictly in
/// order, so none of that state needs a lock: same shape as <see cref="Telemetry.TelemetryWriter"/>, per
/// notes/concurrency-model.md.
/// </summary>
/// <remarks>
/// <b>The Prusa Connect implementation of <see cref="IPrinterLink"/></b>, and deliberately wider
/// than it: everything here beyond the link's two members is wire machinery that only this edge
/// - the session, the transfer engine, the query path - has any business calling. Consumers hold
/// the link.
/// </remarks>
public interface IPrinterConnectionActor : IPrinterLink
{
    /// <summary>Completes once the mailbox has been completed <b>and</b> drained - the actor's
    /// equivalent of <see cref="Telemetry.TelemetryWriter"/>'s shutdown-by-completion.</summary>
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
}
