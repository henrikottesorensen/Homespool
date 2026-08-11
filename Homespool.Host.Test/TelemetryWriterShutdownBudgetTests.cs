using System;

using AwesomeAssertions;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

/// <summary>
/// The shutdown budget spans three files and is enforced by none of them at runtime, so it is
/// pinned here instead.
/// </summary>
/// <remarks>
/// <para>
/// Three values have to stay ordered: <see cref="TelemetryWriter.MaxShutdownFlushDuration"/> (the
/// writer's worst case, every final-flush attempt timing out) fits inside
/// <c>HostOptions.ShutdownTimeout</c>, which fits inside <c>compose.yaml</c>'s
/// <c>stop_grace_period</c>. Only the first two are code; the outer one is deployment
/// configuration, which is why the ceiling below is written as a literal and cross-referenced
/// rather than derived.
/// </para>
/// <para>
/// The ordering is not hypothetical. Measured 2026-07-30 with an outside connection holding the
/// write lock: three attempts that could each block ~10 s summed to ~30 s, landing exactly on the
/// framework's default <c>ShutdownTimeout</c>, so the process was killed part-way through its drain
/// - losing the buffered telemetry and, worse, the log line that would have said how much was lost.
/// Docker's own 10 s default would have killed it sooner still. The failure mode is silent by
/// construction: nothing logs when the thing that would have logged is what got killed.
/// </para>
/// <para>
/// So this test exists to fail loudly if someone raises <c>FinalFlushAttempts</c> or the per-attempt
/// budget without revisiting the two outer numbers.
/// </para>
/// </remarks>
public class TelemetryWriterShutdownBudgetTests
{
    /// <summary>
    /// <c>compose.yaml</c>'s <c>stop_grace_period</c>. Update both together, never one alone.
    /// </summary>
    private static readonly TimeSpan ContainerStopGracePeriod = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Room for everything else a shutdown does before and after the drain - Kestrel finishing its
    /// in-flight requests, the other hosted services, process teardown - plus the margin that keeps
    /// a SIGKILL from landing on the drain's last attempt.
    /// </summary>
    private static readonly TimeSpan NonDrainShutdownAllowance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The flush already running when SIGTERM arrives, which finishes on the ordinary
    /// <see cref="Homespool.Data.StorageOptions.BusyTimeoutMilliseconds"/> budget before the loop can
    /// act on the shutdown at all. Easy to forget, and forgetting it is what left a measured
    /// shutdown at 11 s against an 11 s timeout - killed, with no report, exactly as before.
    /// </summary>
    private static readonly TimeSpan InFlightFlushAllowance = TimeSpan.FromSeconds(5);

    [Fact]
    public void TheWholeShutdownDrainFitsInsideTheContainerStopGracePeriod()
    {
        (TelemetryWriter.MaxShutdownFlushDuration + InFlightFlushAllowance)
            .Should().BeLessThan(ContainerStopGracePeriod - NonDrainShutdownAllowance,
                                 "the drain has to finish and report what it lost before the container runtime SIGKILLs it - "
                                 + "a shutdown killed mid-flush loses the buffers and the record of losing them");
    }

    /// <summary>
    /// The budget must also stay worth having: patient enough that a lock which clears in a second
    /// or two is still ridden out rather than abandoned.
    /// </summary>
    /// <remarks>
    /// The cost of giving up early is data that a slightly longer wait would have saved, on the one
    /// flush with nothing behind it. A floor here stops a future "just make shutdown faster" from
    /// quietly trading that away.
    /// </remarks>
    [Fact]
    public void TheShutdownFlushIsStillPatientEnoughToRideOutBriefContention()
    {
        TelemetryWriter.MaxShutdownFlushDuration
                       .Should().BeGreaterThan(TimeSpan.FromSeconds(5),
                                               "a shutdown that gives up almost immediately discards data a short wait would have saved");
    }
}
