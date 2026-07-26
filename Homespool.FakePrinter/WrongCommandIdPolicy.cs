using System.Collections.Generic;

namespace Homespool.FakePrinter;

/// <summary>
/// Answers correctly but under the wrong command id (the received id plus one) - the stray-ack
/// case the server's correlator must ignore rather than complete the wrong command with.
/// </summary>
public sealed class WrongCommandIdPolicy : CommandAnswerPolicy
{
    private readonly CommandAnswerPolicy _inner;

    /// <summary>Wraps <paramref name="inner"/>, shifting every answered command id by one.</summary>
    public WrongCommandIdPolicy(CommandAnswerPolicy inner)
    {
        _inner = inner;
    }

    /// <inheritdoc/>
    public override IReadOnlyList<PlannedReply> Answer(ServerCommandFrame frame, FakeDevice device)
    {
        // Hand the inner policy a frame that lies about its id: every ack it builds then carries
        // the shifted id, while the server waits on the real one.
        return _inner.Answer(frame with { CommandId = frame.CommandId + 1 }, device);
    }
}
