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
/// order, so none of that state needs a lock: same shape as <see cref="Telemetry.TelemetryWriter"/>.
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

    /// <summary>
    /// Whether this printer's connection can carry the inline transfer engine - chunks pulled over
    /// the same channel its commands arrive on. True for a socket; false for the pre-websocket HTTP
    /// transport, whose printer must be sent an encrypted download to fetch itself instead.
    /// </summary>
    /// <remarks>
    /// Derived from the connection's type - <see cref="IChunkStreamingConnection"/> or not - rather
    /// than declared, so it cannot disagree with what the actor can actually do. This is the check
    /// that lets a sender choose between <c>START_CONNECT_DOWNLOAD</c> and
    /// <c>START_ENCRYPTED_DOWNLOAD</c>; sending the former to a printer that cannot stream chunks
    /// starts a transfer whose chunk request has no address, and firmware asserts on it.
    /// </remarks>
    bool CanStreamChunks { get; }

    /// <summary>
    /// What is at the other end, as it announced itself - see <see cref="PrinterClient"/>.
    /// </summary>
    /// <remarks>
    /// Defaulted to an unannounced socket client, so an implementation that has not thought about the
    /// question behaves like firmware. The direction matters: the plaintext download exists for a
    /// client that positively identifies itself, and a new implementer must not acquire it silently.
    /// </remarks>
    PrinterClient Client => PrinterClient.Anonymous(PrinterTransport.WebSocket);

    /// <summary>
    /// Which variant of the Connect protocol this client speaks, which is what decides how a file
    /// may be handed to it. See <see cref="PrinterDialect"/>.
    /// </summary>
    /// <remarks>
    /// Defaulted to firmware on a socket, so an implementation that has not thought about it behaves
    /// like the client this protocol was written for rather than acquiring a newer path silently.
    /// </remarks>
    PrinterDialect Dialect => PrinterDialect.BuddySocket;

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
