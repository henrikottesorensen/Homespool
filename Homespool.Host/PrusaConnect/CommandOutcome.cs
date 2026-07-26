namespace Homespool.Host.PrusaConnect;

/// <summary>
/// The printer's answer to a sent command, e.g. <c>(Finished, null)</c> or
/// <c>(Rejected, "No print to pause")</c> - confirmed against firmware source
/// (Prusa-Firmware-Buddy planner.cpp:667-790 at the pinned ref): a command's outcome always arrives
/// as an ordinary event carrying the same <c>command_id</c>, not a distinct reply channel.
/// </summary>
public sealed record CommandOutcome(Model.Events EventType, string? Reason);

public enum CommandSendOutcome
{
    Completed,
    NotConnected,
    AlreadyInFlight,
    TimedOut,
}

/// <summary><see cref="CommandOutcome"/> is only present for <see cref="CommandSendOutcome.Completed"/> -
/// the other outcomes are precisely the cases where the printer never answered.</summary>
public sealed record CommandSendResult(CommandSendOutcome Outcome, CommandOutcome? Response);
