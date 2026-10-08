using System;

using AwesomeAssertions;

using Homespool.Data;
using Homespool.Host.Telemetry;

namespace Homespool.Host.Test;

/// <summary>
/// The outer pair of the shutdown limits spans code and deployment configuration, and nothing
/// enforces it at runtime, so it is pinned here instead.
/// </summary>
/// <remarks>
/// <para>
/// Three limits sit each inside the next: <see cref="TelemetryWriter"/>'s shutdown deadline, which it
/// takes from <see cref="Program.ShutdownTimeout"/> and keeps to while it runs; that timeout; and
/// <c>compose.yaml</c>'s <c>stop_grace_period</c>, after which the container runtime SIGKILLs the
/// process. The inner pair holds by construction and is tested against a held lock in
/// <c>TelemetryWriterTests</c> and <c>TelemetryInMemoryStoreTests</c>. The outer pair is a number in
/// a file that is not code, which is why the ceiling below is a literal and cross-referenced rather
/// than derived.
/// </para>
/// <para>
/// The failure mode is silent by construction: a process killed mid-drain loses the buffered
/// telemetry and the log line that would have said how much was lost.
/// </para>
/// </remarks>
public class TelemetryWriterShutdownBudgetTests
{
    /// <summary>
    /// <c>compose.yaml</c>'s <c>stop_grace_period</c>. Update both together, never one alone.
    /// </summary>
    private static readonly TimeSpan ContainerStopGracePeriod = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Room for what happens after the host stops waiting - <c>Main</c> returning, the log flushed,
    /// the process exiting - plus the margin that keeps a SIGKILL from landing on the writer's report.
    /// </summary>
    private static readonly TimeSpan AfterHostTimeoutAllowance = TimeSpan.FromSeconds(2);

    [Fact]
    public void TheHostsShutdownTimeoutEndsInsideTheContainerStopGracePeriod()
    {
        (Program.ShutdownTimeout + AfterHostTimeoutAllowance)
            .Should().BeLessThan(ContainerStopGracePeriod,
                                 "the writer reports what it lost just before the host stops waiting, and that report has to " +
                                 "be written before the container runtime SIGKILLs the process");
    }

    /// <summary>
    /// The timeout must also stay worth having: after the flush already running when shutdown begins,
    /// there has to be time left for a final save to ride out a brief lock.
    /// </summary>
    /// <remarks>
    /// That flush runs to the ordinary <see cref="StorageOptions.BusyTimeoutMilliseconds"/> and is not
    /// shortened, so it can take that much of the deadline before the final save starts. Five seconds
    /// beyond it covers one full three-second save, the writer's one-second reserve and the half
    /// second it keeps back for a save running late. A floor here stops a future "just make shutdown
    /// faster" from quietly giving the last save nothing.
    /// </remarks>
    [Fact]
    public void TheShutdownTimeoutLeavesAFinalSaveTimeAfterTheFlushInFlight()
    {
        TimeSpan inFlightFlush = TimeSpan.FromMilliseconds(new StorageOptions().BusyTimeoutMilliseconds);

        (Program.ShutdownTimeout - inFlightFlush)
            .Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(5),
                                             "a shutdown that gives up almost immediately discards data a short wait would have saved");
    }
}
