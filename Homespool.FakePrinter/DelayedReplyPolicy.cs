using System;
using System.Collections.Generic;
using System.Linq;

namespace Homespool.FakePrinter;

/// <summary>
/// Answers like the inner policy, but late - the printer that is warming up or homing and defers
/// its ack. Models the open question from the 2026-07-25 hardware session: a command issued during
/// warm-up could plausibly exceed the server's response timeout while the printer is merely busy.
/// </summary>
public sealed class DelayedReplyPolicy : CommandAnswerPolicy
{
    private readonly CommandAnswerPolicy _inner;
    private readonly TimeSpan _delay;

    /// <summary>Wraps <paramref name="inner"/>, adding <paramref name="delay"/> to its first reply.</summary>
    public DelayedReplyPolicy(CommandAnswerPolicy inner, TimeSpan delay)
    {
        _inner = inner;
        _delay = delay;
    }

    /// <inheritdoc/>
    public override IReadOnlyList<PlannedReply> Answer(ServerCommandFrame frame, FakeDevice device)
    {
        return _inner.Answer(frame, device)
            .Select((reply, index) => index == 0 ? reply with { Delay = reply.Delay + _delay } : reply)
            .ToList();
    }
}
