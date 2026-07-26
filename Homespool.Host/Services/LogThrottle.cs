using System;
using System.Diagnostics;
using System.Threading;

namespace Homespool.Host.Services;

/// <summary>
/// Caps how often a per-message log site may actually log: the first occurrence is elected
/// immediately, then at most one caller per <see cref="Interval"/> wins the right to log a summary
/// carrying everything recorded in between. Occurrences are always counted exactly
/// (<see cref="Total"/>); only the logging is throttled.
/// </summary>
/// <remarks>
/// <para>
/// Exists because per-occurrence logging on a wire-rate path failed its first load test: 20 seconds
/// of blast telemetry produced 722,973 drop warnings and a 1.0 GB log - a self-feeding cycle, since
/// the log I/O runs on the producing thread and steals exactly the capacity the overloaded
/// component is short of. A normal printer never drives these paths; an attacker can, at wire rate,
/// which makes an unthrottled Warning/Error site a log-disk denial of service
/// (notes/fake-printer-harness.md, the blast run and the audit table).
/// </para>
/// <para>
/// Thread-safe and lock-free: any number of threads may <see cref="Record"/> concurrently; a
/// <see cref="Interlocked.CompareExchange(ref long, long, long)"/> on the last-elected timestamp
/// picks exactly one winner per window. Window counts may smear by an occurrence or two under
/// contention; the lifetime total never does. A burst that stops mid-window leaves its tail count
/// unlogged until the next occurrence, whenever that is - callers that need the exact figure
/// regardless expose <see cref="Total"/> somewhere unthrottled, the way
/// <c>TelemetryHealthSnapshot.DroppedMessages</c> does.
/// </para>
/// </remarks>
public sealed class LogThrottle
{
    private long _total;
    private long _sinceLastElection;
    private long _lastElectionTimestamp;

    /// <summary>Creates a throttle allowing at most one elected log line per <paramref name="interval"/>.</summary>
    public LogThrottle(TimeSpan interval)
    {
        Interval = interval;
    }

    /// <summary>
    /// Minimum spacing between elected occurrences. Mutable only so owners can forward an
    /// <c>init</c> test seam (e.g. <c>TelemetryWriter.DropWarningInterval</c>); set it before the
    /// first <see cref="Record"/> and leave it alone after.
    /// </summary>
    public TimeSpan Interval { get; set; }

    /// <summary>Exact number of occurrences recorded since construction.</summary>
    public long Total => Interlocked.Read(ref _total);

    /// <summary>
    /// Records one occurrence. Non-null when this caller is elected to log - the window says what
    /// to say - and null when the occurrence was counted but this is not the moment to log it.
    /// </summary>
    public LogThrottleWindow? Record()
    {
        Interlocked.Increment(ref _total);
        Interlocked.Increment(ref _sinceLastElection);

        long now = Stopwatch.GetTimestamp();
        long last = Interlocked.Read(ref _lastElectionTimestamp);

        if (last != 0 && Stopwatch.GetElapsedTime(last, now) < Interval)
        {
            return null;
        }

        if (Interlocked.CompareExchange(ref _lastElectionTimestamp, now, last) != last)
        {
            return null;
        }

        return new LogThrottleWindow(
            IsFirstOccurrence: last == 0,
            Count: Interlocked.Exchange(ref _sinceLastElection, 0),
            Elapsed: last == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(last, now),
            Total: Interlocked.Read(ref _total));
    }
}
