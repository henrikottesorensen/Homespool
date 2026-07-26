using System.Collections.Generic;

namespace Homespool.FakePrinter;

/// <summary>
/// Rejects every command with a fixed reason - for pinning how the server surfaces a printer's
/// rejection (outcome and reason string) without needing the device in a particular state.
/// </summary>
public sealed class RejectAllPolicy : CommandAnswerPolicy
{
    private readonly string _reason;

    /// <summary>Creates the policy with the reason every rejection will carry.</summary>
    public RejectAllPolicy(string reason)
    {
        _reason = reason;
    }

    /// <inheritdoc/>
    public override IReadOnlyList<PlannedReply> Answer(ServerCommandFrame frame, FakeDevice device)
    {
        return [new PlannedReply(EventMessageBuilder.Build("REJECTED", device.WireState, frame.CommandId, _reason))];
    }
}
