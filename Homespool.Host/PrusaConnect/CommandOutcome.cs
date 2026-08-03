namespace Homespool.Host.PrusaConnect;

/// <summary>
/// The printer's answer to a sent command, e.g. <c>(Finished, null)</c> or
/// <c>(Rejected, "No print to pause")</c> - confirmed against firmware source
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
public sealed record CommandOutcome(Model.Events EventType, string? Reason);

/// <summary>
/// A <see cref="CommandOutcome"/> from a command that asked the printer a question, with the answer
/// parsed into the shape that command declared.
/// </summary>
/// <typeparam name="TAnswer">
/// The answering event's <c>data</c>, from
/// <see cref="Commands.ISendableCommand{TAnswer}"/>.
/// </typeparam>
/// <param name="EventType">As <see cref="CommandOutcome.EventType"/>.</param>
/// <param name="Reason">As <see cref="CommandOutcome.Reason"/>.</param>
/// <param name="Answer">
/// The parsed payload, or null when the printer answered without one - which a <c>Rejected</c>
/// always does. <b>A verdict is still an answer</b>: a refusal arrives here with
/// <see cref="EventType"/> set and this null, and callers must read the two together rather than
/// treating null as failure.
/// </param>
public sealed record CommandOutcome<TAnswer>(Model.Events EventType, string? Reason, TAnswer? Answer);
