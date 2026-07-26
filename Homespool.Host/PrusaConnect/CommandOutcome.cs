namespace Homespool.Host.PrusaConnect;

/// <summary>
/// The printer's answer to a sent command, e.g. <c>(Finished, null)</c> or
/// <c>(Rejected, "No print to pause")</c> - confirmed against firmware source
/// (Prusa-Firmware-Buddy planner.cpp:667-790 at the pinned ref): a command's outcome always arrives
/// as an ordinary event carrying the same <c>command_id</c>, not a distinct reply channel.
/// </summary>
/// <param name="EventType">
/// The event the printer answered with - <c>Finished</c>, <c>Rejected</c> or <c>StateChanged</c>.
/// All three are real answers; only <c>Rejected</c> means the printer declined.
/// </param>
/// <param name="Reason">
/// The printer's own words when it rejected the command, e.g. "No print to pause". Null for the
/// outcomes that carry no explanation, which is most of them.
/// </param>
public sealed record CommandOutcome(Model.Events EventType, string? Reason);

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
