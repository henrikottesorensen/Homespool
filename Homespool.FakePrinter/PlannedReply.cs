using System;

namespace Homespool.FakePrinter;

/// <summary>
/// One reply a <see cref="CommandAnswerPolicy"/> schedules for a received command: an optional
/// payload to send after an optional delay, and optionally an abrupt disconnect afterwards (the
/// "printer died mid-command" case no real printer will produce on demand).
/// </summary>
/// <param name="Payload">The raw message to send, or null to send nothing (delay/disconnect only).</param>
/// <param name="Delay">Wait before sending; zero replies immediately, like a healthy printer.</param>
/// <param name="DisconnectAfter">Abort the socket - no close handshake - after this reply.</param>
public sealed record PlannedReply(byte[]? Payload, TimeSpan Delay = default, bool DisconnectAfter = false)
{
    /// <summary>
    /// Runs when the reply comes due, applies the command's effect on the device, and renders the
    /// payload then rather than when the command arrived. Takes the place of <see cref="Payload"/>.
    /// </summary>
    /// <remarks>
    /// For a background command whose answer reports the machine as it is <i>afterwards</i>: an
    /// <c>M702</c>'s <c>FINISHED</c> carries the state the unload ended on, which does not exist yet
    /// when the <c>ACCEPTED</c> goes out.
    /// </remarks>
    public Func<byte[]>? Complete { get; init; }

    /// <summary>
    /// Runs once the payload has been sent, for what the device must keep reporting until the
    /// printer has said so - a background command stays named in telemetry until its answer is out.
    /// </summary>
    public Action? Sent { get; init; }
}
