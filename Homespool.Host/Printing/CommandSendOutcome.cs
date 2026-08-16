namespace Homespool.Host.Printing;

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
    Undefined = 0,

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
    /// Only reachable for commands declaring <see cref="PrusaConnect.Commands.ISendableCommand.ExpectsReply"/>
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
