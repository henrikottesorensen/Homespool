using System.Collections.Generic;

namespace Homespool.FakePrinter;

/// <summary>
/// Aborts the connection the moment a command arrives, without answering and without a close
/// handshake - the printer that dies mid-command. The server's pending command must fail as
/// disconnected, not hang until the response timeout.
/// </summary>
public sealed class DisconnectOnCommandPolicy : CommandAnswerPolicy
{
    /// <inheritdoc/>
    public override IReadOnlyList<PlannedReply> Answer(ServerCommandFrame frame, FakeDevice device)
    {
        return [new PlannedReply(Payload: null, DisconnectAfter: true)];
    }
}
