using System.Collections.Generic;
using System.Linq;

namespace Homespool.FakePrinter;

/// <summary>
/// Sends every reply twice - two acks for one command, which the server must survive by ignoring
/// the second rather than faulting or completing something else with it.
/// </summary>
public sealed class DoubleReplyPolicy : CommandAnswerPolicy
{
    private readonly CommandAnswerPolicy _inner;

    /// <summary>Wraps <paramref name="inner"/>, duplicating its reply list.</summary>
    public DoubleReplyPolicy(CommandAnswerPolicy inner)
    {
        _inner = inner;
    }

    /// <inheritdoc/>
    public override IReadOnlyList<PlannedReply> Answer(ServerCommandFrame frame, FakeDevice device)
    {
        IReadOnlyList<PlannedReply> replies = _inner.Answer(frame, device);

        return replies.Concat(replies).ToList();
    }
}
