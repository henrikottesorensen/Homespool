using System.Collections.Generic;

namespace Homespool.FakePrinter;

/// <summary>
/// Decides how the fake answers a server command. <see cref="FirmwareFaithfulPolicy"/> is the
/// default and answers like the firmware; the other implementations decorate or replace it with
/// the misbehaviours a real printer refuses to produce - the response-timeout path, mismatched and
/// duplicated acks, mid-command disconnects (see <c>notes/fake-printer-harness.md</c>, "Cases a
/// real printer refuses to produce").
/// </summary>
public abstract class CommandAnswerPolicy
{
    /// <summary>
    /// Plans the replies for one received command. Called sequentially from the connection's single
    /// read loop, so implementations may keep unguarded per-connection state (dedup ids, a busy
    /// window) the way the firmware's planner does.
    /// </summary>
    public abstract IReadOnlyList<PlannedReply> Answer(ServerCommandFrame frame, FakeDevice device);
}
