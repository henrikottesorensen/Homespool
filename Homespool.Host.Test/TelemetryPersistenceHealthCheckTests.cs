using System;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;
using Homespool.Host.PrusaConnect;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="TelemetryPersistenceHealthCheck"/> - the grading of the writer's own state into
/// something a monitoring system can act on.
/// </summary>
/// <remarks>
/// The distinction being tested is between recoverable and lost. A stuck database still holding
/// everything in memory is Degraded and answers 200, because restarting the service would not
/// unstick it and would throw away the buffer. Discarded events are gone, and that is the state
/// worth waking someone for.
/// </remarks>
public class TelemetryPersistenceHealthCheckTests
{
    private sealed class StubHealthSource : ITelemetryHealthSource
    {
        public TelemetryHealthSnapshot Current { get; set; } = TelemetryHealthSnapshot.Initial;

        public bool IsDraining { get; set; } = true;
    }

    private static async Task<HealthCheckResult> CheckAsync(TelemetryHealthSnapshot snapshot)
    {
        StubHealthSource source = new() { Current = snapshot };

        return await new TelemetryPersistenceHealthCheck(source)
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
    }

    /// <summary>
    /// A process that has never flushed is idle, not broken - nothing has connected yet. Reporting
    /// Unhealthy here would make every fresh deployment fail its probe before the first printer
    /// arrives.
    /// </summary>
    [Fact]
    public async Task NeverHavingFlushedIsHealthy()
    {
        HealthCheckResult result = await CheckAsync(TelemetryHealthSnapshot.Initial);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task FlushingNormallyIsHealthy()
    {
        HealthCheckResult result = await CheckAsync(
            new TelemetryHealthSnapshot(DateTimeOffset.UtcNow, ConsecutiveFailures: 0, PendingSamples: 3, PendingEvents: 0, DiscardedEvents: 0));

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task ARecentFailureIsDegradedRatherThanUnhealthy()
    {
        HealthCheckResult result = await CheckAsync(
            new TelemetryHealthSnapshot(DateTimeOffset.UtcNow, ConsecutiveFailures: 1, PendingSamples: 12, PendingEvents: 2, DiscardedEvents: 0));

        result.Status.Should().Be(HealthStatus.Degraded,
            "a database that is briefly locked is not something a restart would fix, and the data is still buffered");
    }

    [Fact]
    public async Task SustainedFailureIsUnhealthy()
    {
        HealthCheckResult result = await CheckAsync(
            new TelemetryHealthSnapshot(DateTimeOffset.UtcNow.AddMinutes(-5), ConsecutiveFailures: 10, PendingSamples: 400, PendingEvents: 9, DiscardedEvents: 0));

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    /// <summary>
    /// The state the whole check exists for: writes have been failing long enough that events have
    /// been dropped to cap memory, so data is gone rather than merely late.
    /// </summary>
    [Fact]
    public async Task DiscardedEventsAreUnhealthyEvenOnceFlushesRecover()
    {
        HealthCheckResult result = await CheckAsync(
            new TelemetryHealthSnapshot(DateTimeOffset.UtcNow, ConsecutiveFailures: 0, PendingSamples: 0, PendingEvents: 0, DiscardedEvents: 12));

        result.Status.Should().Be(HealthStatus.Unhealthy,
            "recovering afterwards does not bring the discarded events back, and nothing else records that they were lost");
    }

    /// <summary>
    /// The counters ride along whatever the verdict, because "Unhealthy" alone cannot tell a stuck
    /// database from one that has already lost data.
    /// </summary>
    [Fact]
    public async Task TheCountersAreReportedAlongsideTheVerdict()
    {
        HealthCheckResult result = await CheckAsync(
            new TelemetryHealthSnapshot(DateTimeOffset.UtcNow, ConsecutiveFailures: 3, PendingSamples: 41, PendingEvents: 7, DiscardedEvents: 0));

        result.Data.Should().Contain("pendingSamples", 41)
                            .And.Contain("pendingEvents", 7)
                            .And.Contain("consecutiveFailures", 3);
    }
}
