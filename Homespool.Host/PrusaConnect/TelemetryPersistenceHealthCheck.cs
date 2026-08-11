using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using Homespool.Data;

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

    /// <summary>
    /// How many flush intervals may pass with no completed flush before the writer is called stuck.
    /// </summary>
    /// <remarks>
    /// Ten, matching <see cref="UnhealthyAfterConsecutiveFailures"/>, because the consequence is
    /// identical - nothing is reaching the database - and an operator should not have to learn two
    /// tolerances. The floor stops a small configured interval making this trigger-happy.
    /// </remarks>
    private const int StaleAfterMissedFlushIntervals = 10;

    /// <summary>Shortest staleness that may ever be called stuck, whatever the flush interval.</summary>
    private static readonly TimeSpan MinimumStaleThreshold = TimeSpan.FromSeconds(15);

    private readonly ITelemetryHealthSource _source;
    private readonly UnknownFieldTracker _unknownFields;
    private readonly StorageOptions _storage;
    private readonly TimeProvider _timeProvider;

    public TelemetryPersistenceHealthCheck(ITelemetryHealthSource source,
                                           UnknownFieldTracker unknownFields,
                                           IOptions<StorageOptions> storage,
                                           TimeProvider timeProvider)
    {
        _source = source;
        _unknownFields = unknownFields;
        _storage = storage.Value;
        _timeProvider = timeProvider;
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
            ["droppedMessages"] = snapshot.DroppedMessages,
            ["discardedEvents"] = snapshot.DiscardedEvents,

            // Reported, never graded. An unmodelled wire field means this build is discarding
            // something a printer said - worth an operator seeing, but not a persistence fault, and
            // grading it would send the alert email for a firmware upgrade. The log carries the
            // first sighting of each name; this is the unthrottled exact total.
            //
            // The count only, deliberately - never UnknownFieldTracker.DistinctFields. This endpoint
            // is anonymous, on the stated grounds that it carries "only counters and timestamps about
            // this service's own write path" (Program.cs). Field names are neither: they come off the
            // wire, so publishing them would let anyone who can reach /p/ws inject chosen strings and
            // read them back from an unauthenticated endpoint. A monotonic counter is what a
            // monitoring system needs; the names belong in the log and, later, behind admin auth.
            ["unknownFieldOccurrences"] = _unknownFields.Total,
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

        // A writer can be stuck without ever failing, and until 2026-07-29 nothing here could see it.
        // A held write lock makes a flush *block* rather than fail - Microsoft.Data.Sqlite retries
        // SQLITE_BUSY internally - so ConsecutiveFailures stays 0 and every branch above passes.
        // Measured with an outside connection holding BEGIN IMMEDIATE: 23 s of a completely stalled
        // writer, reported Healthy throughout, then a shutdown killed mid-drain (tools/slow-db,
        // MECHANISM=lock). The comment that used to sit here - "only failures distinguish idle from
        // stuck" - was the assumption that made it invisible.
        //
        // LastFlushAt is the signal, and it works because a flush with nothing buffered still counts:
        // FlushAsync returns before it opens a context, SafeFlushAsync records the time, so an idle
        // deployment refreshes this every WriteFlushIntervalSeconds without touching the database.
        // Staleness therefore means "the drain loop is not completing flushes", not "there was
        // nothing to write" - which is exactly the distinction the old comment thought impossible.
        //
        // Note what this deliberately does not catch: an idle process whose database is unreachable
        // stays Healthy, because no-op flushes keep succeeding. That is the right answer - nothing is
        // failing to be persisted while there is nothing to persist - and the moment real work
        // arrives, the first blocked flush starts the clock.
        TimeSpan staleAfter = Max(
            TimeSpan.FromSeconds(_storage.WriteFlushIntervalSeconds * StaleAfterMissedFlushIntervals),
            MinimumStaleThreshold);

        if (snapshot.LastFlushAt is { } lastFlush && _timeProvider.GetUtcNow() - lastFlush > staleAfter)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                                       $"No telemetry flush has completed for {(_timeProvider.GetUtcNow() - lastFlush).TotalSeconds:F0}s, with none failing either - the writer is blocked rather than broken, and nothing is reaching the database.",
                                       data: data));
        }

        // Never having flushed is not a fault: a process with no printers connected has had nothing
        // to write, and the first no-op flush is at most one flush interval away in any case.
        return Task.FromResult(HealthCheckResult.Healthy("Telemetry is being persisted.", data));
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right)
    {
        return left > right ? left : right;
    }
}
