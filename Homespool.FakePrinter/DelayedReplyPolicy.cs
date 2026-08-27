using System;
using System.Collections.Generic;
using System.Linq;

namespace Homespool.FakePrinter;

/// <summary>
/// Answers like the inner policy, but late - the printer that is warming up or homing and defers
/// its ack. Models the open question from the 2026-07-25 hardware session: a command issued during
/// warm-up could plausibly exceed the server's response timeout while the printer is merely busy.
/// </summary>
/// <remarks>
/// <b>That question was answered on 2026-08-21, and against us:</b> a real <c>START_PRINT</c>
/// exceeded the timeout on a printer that had
/// accepted it and gone off to home and heat, and the queue recorded the print as not having
/// happened. So this is no longer a hypothetical to model - it is the shape of a defect, and the
/// reason it can be reproduced end to end.
/// </remarks>
public sealed class DelayedReplyPolicy : CommandAnswerPolicy
{
    private readonly CommandAnswerPolicy _inner;
    private readonly TimeSpan _delay;
    private readonly IReadOnlySet<string>? _commands;

    /// <summary>Wraps <paramref name="inner"/>, adding <paramref name="delay"/> to its first reply.</summary>
    /// <param name="inner">The policy that decides what to answer.</param>
    /// <param name="delay">How much later than usual the first reply goes out.</param>
    /// <param name="commands">
    /// The wire names to delay, or null - the default - for all of them.
    /// </param>
    /// <remarks>
    /// <b>The filter is what makes a slow printer testable rather than merely a broken one.</b>
    /// Hardware defers the ack of the command that <i>set it working</i>; everything asked of it
    /// before and after answers at ordinary speed. A double that delayed every command would take the
    /// whole exchange down with it - including the questions the server asks precisely <i>because</i>
    /// one command went unanswered - so the interesting case could never be reached.
    /// </remarks>
    public DelayedReplyPolicy(CommandAnswerPolicy inner, TimeSpan delay, IReadOnlySet<string>? commands = null)
    {
        _inner = inner;
        _delay = delay;
        _commands = commands;
    }

    /// <inheritdoc/>
    public override IReadOnlyList<PlannedReply> Answer(ServerCommandFrame frame, FakeDevice device)
    {
        ArgumentNullException.ThrowIfNull(frame);

        IReadOnlyList<PlannedReply> replies = _inner.Answer(frame, device);

        // The state change is the inner policy's and has already happened, whatever this does to the
        // timing - which is the fidelity that matters here: the printer really did accept the print
        // it was slow to acknowledge.
        return _commands is not null && !_commands.Contains(frame.TryGetJsonCommandName() ?? string.Empty) ?
            replies :
            replies.Select((reply, index) => index == 0 ? reply with { Delay = reply.Delay + _delay } : reply)
                   .ToList();
    }
}
