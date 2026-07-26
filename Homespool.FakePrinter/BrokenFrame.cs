namespace Homespool.FakePrinter;

/// <summary>
/// A frame the firmware would refuse before dispatch, with the reason string it would put in the
/// resulting <c>REJECTED</c> event and the command id (0 when the header itself was unreadable)
/// that event would carry. Reasons are verbatim from <c>connect.cpp</c> at the pinned ref.
/// </summary>
/// <param name="CommandId">The id the rejection is reported under - 0 for an unreadable header.</param>
/// <param name="Reason">The firmware's <c>BrokenCommand</c> reason string, verbatim.</param>
public sealed record BrokenFrame(uint CommandId, string Reason);
