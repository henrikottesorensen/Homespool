using System.Collections.Generic;

namespace Homespool.FakePrinter;

/// <summary>
/// Never answers anything - the printer that goes silent on a command. This is the policy that
/// exercises the server's response-timeout path, which has never executed against hardware because
/// the real MK3.5 always answers.
/// </summary>
public sealed class NoReplyPolicy : CommandAnswerPolicy
{
    /// <inheritdoc/>
    public override IReadOnlyList<PlannedReply> Answer(ServerCommandFrame frame, FakeDevice device)
    {
        return [];
    }
}
