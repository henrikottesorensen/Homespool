using System;

namespace PrinterService.Host.PrusaConnect;

/// <summary>
/// What <see cref="TelemetryWriter"/> knows about its own ability to persist, read by
/// <see cref="TelemetryPersistenceHealthCheck"/>.
/// </summary>
/// <remarks>
/// Deliberately a state snapshot rather than anything log-derived. The writer already holds these
/// facts; recovering them by matching levels and message text in its own output would depend on
/// Serilog configuration - which has silently swallowed whole namespaces in this project before -
/// and could not tell a single transient failure from a stuck one.
/// </remarks>
/// <param name="LastFlushAt">When a flush last succeeded, or null if none has yet. Null is normal on
/// a fresh process with nothing to write, so it is not on its own a fault.</param>
/// <param name="ConsecutiveFailures">Failed flushes since the last success. Reset by any success.</param>
/// <param name="PendingSamples">Samples buffered and not yet written.</param>
/// <param name="PendingEvents">Events buffered and not yet written.</param>
/// <param name="DiscardedEvents">Events dropped to cap memory since this process started. Any value
/// above zero is data that no longer exists anywhere.</param>
public sealed record TelemetryHealthSnapshot(
    DateTimeOffset? LastFlushAt,
    int ConsecutiveFailures,
    int PendingSamples,
    int PendingEvents,
    long DiscardedEvents)
{
    /// <summary>A writer that has done nothing yet - the state before the first flush attempt.</summary>
    public static readonly TelemetryHealthSnapshot Initial = new(null, 0, 0, 0, 0);
}
