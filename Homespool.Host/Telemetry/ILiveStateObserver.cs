using System;

namespace Homespool.Host.Telemetry;

/// <summary>
/// Told by <see cref="TelemetryWriter"/> how a printer's live state moved, message by message.
/// </summary>
/// <remarks>
/// <para>
/// <b>The writer's drain loop is the one place a printer's state before and after a message both
/// exist</b> - the per-printer cache is a local of that loop, and by the time anything is flushed the
/// "before" is gone. So something that reacts to a change, rather than to a state, has to be told
/// here.
/// </para>
/// <para>
/// <b>Called on the drain loop, so an implementation must return at once and must not throw.</b> It
/// is handed values, not the cached entity, and may do no more with them than record them: anything
/// slower is somebody else's loop. The writer catches what an implementation throws anyway, because
/// a message dropped for an observer's fault would be a telemetry gap caused by a notification.
/// </para>
/// </remarks>
public interface ILiveStateObserver
{
    /// <summary>
    /// A printer's state as it was before one message and as it is after it.
    /// </summary>
    /// <param name="printerId">The printer.</param>
    /// <param name="before">What the writer held before the message.</param>
    /// <param name="after">What it holds now.</param>
    /// <param name="at">When the message was received.</param>
    void Observed(int printerId, LiveStateSnapshot before, LiveStateSnapshot after, DateTimeOffset at);
}
