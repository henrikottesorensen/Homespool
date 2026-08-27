using System;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Host.PrusaConnect.DTO.Telemetry;
using Homespool.Host.PrusaConnect.DTO.Transfers;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// One message in a <see cref="PrinterConnectionActor"/>'s mailbox. Everything that touches a
/// connection's state - the socket write side, the in-flight command, transfer state once that
/// feature lands - travels as one of these and is processed strictly in order by the actor's loop,
/// which is what makes "never interleave" true by construction.
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
/// <see cref="CommandSendOutcome.ResponseTimedOut"/> without one.
/// </summary>
/// <param name="Command">The command to write to the printer, and the wire name the reply is
/// correlated against.</param>
/// <param name="Completion">
/// How the loop answers the waiting caller - the printer's correlated reply, or a
/// <see cref="CommandSendOutcome"/> that never reached the wire. Completed exactly once, by the
/// loop, including while draining.
/// </param>
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
/// before being handed to <see cref="Telemetry.ITelemetrySink"/> either way.
/// <c>Identity</c> is the parsed identity payload when the event carried one (an <c>INFO</c>) -
/// extracted by <see cref="MessageDispatcher"/>, which owns the unknown-field accounting the parse
/// feeds, and carried here so the actor's enqueue can hand the sink a complete record.
/// </summary>
public sealed record InboundEventMessage(DateTimeOffset ReceivedAt,
                                         EventDTO Event,
                                         Telemetry.PrinterIdentityUpdate? Identity = null) : ConnectionMessage;

/// <summary>Telemetry parsed off the wire, converted to the neutral currency at the actor's
/// enqueue - the last point that knows this is the Prusa edge.</summary>
public sealed record InboundTelemetryMessage(DateTimeOffset ReceivedAt, TelemetryDTO Telemetry) : ConnectionMessage;

/// <summary>
/// The printer asking for the next byte range of an inline file transfer
/// (<c>{"transfer":"inline", ...}</c>, firmware render.cpp:100-119 at the pinned ref). Routed to the
/// actor because transfer state and command-id allocation are the same state and want the same owner
/// (<c>file_id</c> <i>is</i> a command id).
/// </summary>
public sealed record InboundTransferRequestMessage(DateTimeOffset ReceivedAt, InlineRequestDTO Request)
    : ConnectionMessage;

/// <summary>
/// The HTTP transport asking, on behalf of a telemetry POST it is about to answer, whether a
/// command is waiting for this printer. Answered by the loop with the parked command - now
/// delivered, its response clock started - or null for "nothing pending", which the caller turns
/// into a 204.
/// </summary>
/// <remarks>
/// A message rather than a direct read of the connection's slot, so that the slot stays loop-owned:
/// the request thread posts and awaits, and only the loop takes, stamps and hands over. The
/// alternative - a self-synchronised slot the request thread empties itself - would be the first
/// piece of command state touched from two threads, and this class's history is what that costs.
/// </remarks>
public sealed record TakePendingCommandMessage(TaskCompletionSource<PendingCommand?> Completion) : ConnectionMessage;
