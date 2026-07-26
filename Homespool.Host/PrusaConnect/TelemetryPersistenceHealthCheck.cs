using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Reports whether telemetry is actually reaching the database, so a monitoring system can see a
/// stuck writer instead of nobody noticing.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a real incident: a flush bug made every write fail permanently while the
/// service went on accepting telemetry, logging an identical Error every two seconds for six
/// minutes. Nothing consumed that. The process looked completely healthy from outside - it answered
/// requests, held its WebSocket connections, and reported nothing wrong - while discarding
/// everything it was built to record.
/// </para>
/// <para>
/// The three states are graded by whether data has been lost, not by how alarming the failure looks.
/// Buffered-but-unwritten is recoverable, so it is Degraded: a locked database that clears in a
/// minute costs nothing, and paging someone for it would be noise. Discarded events are gone for
/// good, so that is Unhealthy - and Unhealthy is what turns into a non-200 response.
/// </para>
/// </remarks>
public sealed class TelemetryPersistenceHealthCheck : IHealthCheck
{
    /// <summary>
    /// Consecutive failures tolerated before a stuck writer is called Unhealthy rather than merely
    /// Degraded. At the default two-second flush interval this is roughly twenty seconds - longer
    /// than SQLite's busy timeout, so an ordinary lock contention resolves well inside it.
    /// </summary>
    private const int UnhealthyAfterConsecutiveFailures = 10;

    private readonly ITelemetryHealthSource _source;

    public TelemetryPersistenceHealthCheck(ITelemetryHealthSource source)
    {
        _source = source;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        TelemetryHealthSnapshot snapshot = _source.Current;

        // Reported whatever the verdict: a monitoring system that only sees "Unhealthy" cannot tell
        // a stuck database from a lost one, and these are the numbers that distinguish them.
        Dictionary<string, object> data = new()
        {
            ["lastFlushAt"] = snapshot.LastFlushAt?.ToString("O") ?? "never",
            ["consecutiveFailures"] = snapshot.ConsecutiveFailures,
            ["pendingSamples"] = snapshot.PendingSamples,
            ["pendingEvents"] = snapshot.PendingEvents,
            ["discardedEvents"] = snapshot.DiscardedEvents,
        };

        if (snapshot.DiscardedEvents > 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"{snapshot.DiscardedEvents} printer events were discarded to cap memory - the database has been rejecting writes long enough to lose data.",
                data: data));
        }

        if (snapshot.ConsecutiveFailures >= UnhealthyAfterConsecutiveFailures)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"{snapshot.ConsecutiveFailures} consecutive telemetry flushes have failed; nothing is reaching the database.",
                data: data));
        }

        if (snapshot.ConsecutiveFailures > 0)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"{snapshot.ConsecutiveFailures} telemetry flush(es) have failed since the last success; {snapshot.PendingSamples} samples and {snapshot.PendingEvents} events are still buffered.",
                data: data));
        }

        // Never having flushed is not a fault: a process with no printers connected has had nothing
        // to write. Only failures distinguish idle from stuck.
        return Task.FromResult(HealthCheckResult.Healthy("Telemetry is being persisted.", data));
    }
}
