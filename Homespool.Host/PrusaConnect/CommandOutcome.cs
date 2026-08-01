using System.Text.Json;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// The printer's answer to a sent command, e.g. <c>(Finished, null, null)</c> or
/// <c>(Rejected, "No print to pause", null)</c> - confirmed against firmware source
/// (Prusa-Firmware-Buddy planner.cpp:667-790 at the pinned ref): a command's outcome always arrives
/// as an ordinary event carrying the same <c>command_id</c>, not a distinct reply channel.
/// </summary>
/// <param name="EventType">
/// The event the printer answered with - <c>Finished</c>, <c>Rejected</c>, <c>StateChanged</c> or
/// <c>TransferInfo</c>. All are real answers; only <c>Rejected</c> means the printer declined.
/// <c>TransferInfo</c> is how a transfer-starting command succeeds: firmware answers
/// <c>START_CONNECT_DOWNLOAD</c> with a <c>TRANSFER_INFO</c> carrying the command's own id, never
/// with <c>Finished</c> (planner.cpp:801-824). Nothing here has to enumerate them - correlation is
/// on <c>command_id</c> alone - but callers judging success by event type do.
/// </param>
/// <param name="Reason">
/// The printer's own words when it rejected the command, e.g. "No print to pause". Null for the
/// outcomes that carry no explanation, which is most of them.
/// </param>
/// <param name="Data">
/// The answering event's <c>data</c> object, verbatim, or null when it carried none - which is most
/// of them.
/// <para>
/// <b>Why an answer needs a payload at all.</b> For every command sent so far the event <i>type</i>
/// is the whole answer: <c>Finished</c> means it worked and <c>Rejected</c> means it did not.
/// <c>SEND_FILE_INFO</c> is the first where the payload <i>is</i> the answer - a directory listing
/// arrives as the <c>children</c> of a <c>FILE_INFO</c>'s data (protocol-reference.md,
/// "<c>SEND_FILE_INFO</c> on a directory enumerates it"). Without this the caller who asked the
/// question cannot read the reply, even though the event itself is persisted like any other.
/// </para>
/// <para>
/// <b>Verbatim rather than typed, deliberately.</b> Correlation is on <c>command_id</c> alone and
/// nothing on this path interprets an event's shape; a typed answer here would drag every answering
/// event's schema into the transport, to be widened again by the next command that asks a question.
/// The caller knows which shape its own command produces and is the one place that can parse it
/// without guessing.
/// </para>
/// <para>
/// <b>Safe to hold past the read loop.</b> This is <c>EventDTO.Data</c>, which deserialisation backs
/// with a document of its own rather than a buffer the loop goes on to reuse. The sink already
/// depends on that: <c>TelemetryWriter</c> formats the same element off its channel, on another
/// thread, well after the connection has moved on.
/// </para>
/// </param>
public sealed record CommandOutcome(Model.Events EventType, string? Reason, JsonElement? Data);

/// <summary>
/// How far a command got. Every value except <see cref="Completed"/> means the printer's answer is
/// unavailable, but they differ in what is actually known, and callers should not flatten them into
/// "it failed".
/// </summary>
/// <remarks>
/// Read as a sequence of gates the command passes: connected, no other command in flight, written to
/// the socket, answered. Each value names the first gate it failed at, which is also exactly how much
/// can be concluded about whether the printer acted on it.
/// </remarks>
public enum CommandSendOutcome
{
    /// <summary>
    /// The printer answered. <see cref="CommandSendResult.Response"/> carries what it said, which
    /// may still be a refusal - see <see cref="CommandOutcome"/>.
    /// </summary>
    /// <remarks>
    /// The only value for which a response is present, and the only one that says anything about
    /// what the printer actually did.
    /// </remarks>
    Completed,

    /// <summary>
    /// The frame was written, and no answer is expected - the command is one the printer
    /// structurally cannot acknowledge. <see cref="CommandSendResult.Response"/> is null, and that is
    /// success rather than a shortfall.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only reachable for commands declaring <see cref="Commands.ISendableCommand.ExpectsReply"/>
    /// false. <c>RESET_PRINTER</c> is the case that proved this necessary: firmware's handler calls
    /// <c>printer.reset_printer()</c> and the rejection line after it is annotated "We reach this place
    /// only if the reset_printer fails to execute" (planner.cpp:960-966), so a <i>successful</i> reset
    /// never answers - the printer reboots instead.
    /// </para>
    /// <para>
    /// Observed rather than deduced: in a capture, Prusa's own Connect sent <c>RESET_PRINTER</c> nine
    /// times over 57 seconds and got nothing back, because the first one had already taken the printer
    /// down. Without this outcome the same command would sit in the in-flight slot until
    /// <see cref="ResponseTimedOut"/> fired, reporting failure for a command that worked perfectly.
    /// </para>
    /// </remarks>
    Dispatched,

    /// <summary>
    /// No live socket to write to, so the command definitively never left.
    /// </summary>
    /// <remarks>
    /// Either the printer was absent from <see cref="PrinterConnectionRegistry"/> when the send was
    /// attempted, or its connection was torn down while the command sat in the actor's mailbox. The
    /// strongest negative claim in this enum: nothing was written, so nothing happened.
    /// </remarks>
    NotConnected,

    /// <summary>
    /// Another command is already awaiting its reply on this printer, and this one was refused
    /// rather than queued behind it.
    /// </summary>
    /// <remarks>
    /// One in flight per printer is deliberate, not a limitation of this implementation: replies are
    /// correlated by <c>command_id</c> and the firmware answers them one at a time
    /// (connect.cpp:469-476 at the pinned ref). Nothing was written, so the command did not happen -
    /// retrying once the previous one settles is safe.
    /// </remarks>
    AlreadyInFlight,

    /// <summary>
    /// The frame reached the socket, but the printer sent no answering event within
    /// <c>PrusaConnectOptions.CommandResponseTimeout</c>.
    /// </summary>
    /// <remarks>
    /// Says nothing about whether the command was acted on - the bytes demonstrably left, so the
    /// printer most likely has it and is simply slow. The firmware defers acks while warming up or
    /// homing. The connection is unaffected: the next command can go immediately, and a late answer
    /// still arrives and is persisted as an ordinary event.
    /// </remarks>
    ResponseTimedOut,

    /// <summary>
    /// The write to the socket did not finish within <c>PrinterConnectionActor.SendTimeout</c>, so
    /// whether the printer received the command is unknown.
    /// </summary>
    /// <remarks>
    /// The two timeouts sit on opposite sides of the write completing, and mean different things.
    /// <see cref="ResponseTimedOut"/> is the printer being slow to answer something it certainly
    /// received; this is the transport itself failing, with any prefix of the frame possibly sitting
    /// in a peer buffer. It is also not <see cref="NotConnected"/>, which asserts the command never
    /// left - a claim this case cannot make.
    /// <para>
    /// Unlike the other outcomes, this one is terminal for the connection: a write that never
    /// completes means the peer has stopped draining its socket, so the actor abandons the connection
    /// and the printer reconnects.
    /// </para>
    /// </remarks>
    SendTimedOut,
}

/// <summary>
/// What became of one <c>SendCommandAsync</c> call: how far it got, and the printer's answer if it
/// got all the way.
/// </summary>
/// <param name="Outcome">How far the command got. See <see cref="CommandSendOutcome"/>.</param>
/// <param name="Response">
/// The printer's answer, present only for <see cref="CommandSendOutcome.Completed"/> - the other
/// outcomes are precisely the cases where no answer arrived.
/// </param>
public sealed record CommandSendResult(CommandSendOutcome Outcome, CommandOutcome? Response);
