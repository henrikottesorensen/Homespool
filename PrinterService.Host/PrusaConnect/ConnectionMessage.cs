using System;
using System.Threading;
using System.Threading.Tasks;

using PrinterService.Host.PrusaConnect.Commands;
using PrinterService.Host.PrusaConnect.DTO.EventMessages;
using PrinterService.Host.PrusaConnect.DTO.Telemetry;

namespace PrinterService.Host.PrusaConnect;

/// <summary>
/// One message in a <see cref="PrinterConnectionActor"/>'s mailbox. Everything that touches a
/// connection's state - the socket write side, the in-flight command, transfer state once that
/// feature lands - travels as one of these and is processed strictly in order by the actor's loop,
/// which is what makes "never interleave" true by construction (notes/concurrency-model.md).
/// </summary>
public abstract record ConnectionMessage
{
    private protected ConnectionMessage()
    {
    }
}

/// <summary>
/// A request to send <paramref name="Command"/> to the printer, posted from a request thread via
/// <see cref="IPrinterConnectionActor.SendCommandAsync"/>. The actor answers through
/// <paramref name="Completion"/> - with the printer's correlated reply, or with
/// <see cref="CommandSendOutcome.AlreadyInFlight"/>/<see cref="CommandSendOutcome.NotConnected"/>/
/// <see cref="CommandSendOutcome.TimedOut"/> without one.
/// </summary>
/// <param name="CallerToken">
/// The requesting caller's own token, carried so the loop can tell whether anyone is still waiting
/// by the time it reaches this message. Posting and executing are separate steps here, so cancelling
/// the caller ends only its wait - without this, an aborted request's command would still be written
/// to the printer, and would still take the one in-flight slot on the way.
/// </param>
public sealed record SendCommandMessage(
    ISendableCommand Command,
    TaskCompletionSource<CommandSendResult> Completion,
    CancellationToken CallerToken) : ConnectionMessage;

/// <summary>
/// An event parsed off the wire. May answer the in-flight command (matching <c>command_id</c>)
/// before being handed to <see cref="ITelemetrySink"/> either way.
/// </summary>
public sealed record InboundEventMessage(DateTimeOffset ReceivedAt, EventDTO Event) : ConnectionMessage;

/// <summary>Telemetry parsed off the wire, forwarded to <see cref="ITelemetrySink"/> unchanged.</summary>
public sealed record InboundTelemetryMessage(DateTimeOffset ReceivedAt, TelemetryDTO Telemetry) : ConnectionMessage;

/// <summary>
/// The printer asking for the next chunk of an inline file transfer
/// (<c>{"transfer":"inline", ...}</c>, firmware render.cpp:100-119 at the pinned ref). Routed to the
/// actor because transfer state and command-id allocation are the same state and want the same owner
/// (<c>file_id</c> <i>is</i> a command id - notes/transfer-protocol.md); carries no payload yet
/// because serving chunks isn't built, so today the actor only logs it.
/// </summary>
public sealed record InboundTransferRequestMessage : ConnectionMessage;
