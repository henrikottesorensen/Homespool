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
    CancellationToken CallerToken) : ConnectionMessage
{
    private const int Queued = 0;
    private const int Taken = 1;
    private const int Abandoned = 2;

    // The one piece of a send that two threads write, and only once: whichever of the loop and a
    // cancelled caller gets here first decides whether the loop acts on it at all. A caller that wins
    // can leave at once, knowing the command will never be written; one that loses has to ask the
    // loop what happened, because by then the loop may have handed it to the printer.
    private int _state = Queued;

    /// <summary>The loop taking this send. False when its caller abandoned it first.</summary>
    internal bool TryTake()
    {
        return Interlocked.CompareExchange(ref _state, Taken, Queued) == Queued;
    }

    /// <summary>The caller abandoning this send before the loop took it. False once the loop has it.</summary>
    internal bool TryAbandon()
    {
        return Interlocked.CompareExchange(ref _state, Abandoned, Queued) == Queued;
    }
}

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

/// <summary>
/// The HTTP transport giving back a command it took for a telemetry POST that ended before the
/// response was written. The printer never saw it, so the loop parks it again for the next poll.
/// </summary>
/// <remarks>
/// Only a return before the write is possible. Once the response has started, even a write that
/// succeeds proves only that the bytes reached the proxy in front of us, so the command stays
/// delivered and its response clock decides.
/// </remarks>
public sealed record ReturnCollectedCommandMessage(PendingCommand Command) : ConnectionMessage;

/// <summary>
/// The HTTP transport confirming that a command it took is going out in its POST's response: the
/// request was still there once the command was in hand, so nothing will give it back.
/// </summary>
/// <remarks>
/// Every take that hands over a command is followed by exactly one of this or a
/// <see cref="ReturnCollectedCommandMessage"/>, so between the two the loop knows the command is with
/// a poll but not yet whether it will leave. A command that expects no reply is reported sent only on
/// this, and a caller that gave up meanwhile is told the printer has it.
/// <para>
/// <b>Answered, because the loop has the last word.</b> <paramref name="Completion"/> is true when the
/// response may carry the command, and false when the connection is being torn down: the teardown
/// reports the command <see cref="CommandSendOutcome.NotConnected"/>, which is only true if
/// the response then leaves it out.
/// </para>
/// </remarks>
public sealed record CommandDeliveredMessage(PendingCommand Command, TaskCompletionSource<bool> Completion) : ConnectionMessage;

/// <summary>
/// A caller of <see cref="IPrinterConnectionActor.SendCommandAsync"/> whose token was cancelled,
/// asking the loop to settle its send: withdrawn if it has not reached the printer, otherwise
/// reported as having reached it.
/// </summary>
/// <remarks>
/// The loop decides because only the loop knows. The caller's own view of the race - "my token
/// fired" - says nothing about whether a poll had already taken the command, and a sender holding
/// something the printer will come back for has to know which.
/// </remarks>
public sealed record CancelSendMessage(TaskCompletionSource<CommandSendResult> Completion) : ConnectionMessage;
