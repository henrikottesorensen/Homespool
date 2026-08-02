using System;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Diagnostics.HealthChecks;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="TelemetryWriterLivenessHealthCheck"/> - what may and may not justify killing the
/// process.
/// </summary>
public class TelemetryWriterLivenessHealthCheckTests
{
    private sealed class StubHealthSource : ITelemetryHealthSource
    {
        public TelemetryHealthSnapshot Current { get; set; } = TelemetryHealthSnapshot.Initial;

        public bool IsDraining { get; set; } = true;
    }

    private static async Task<HealthCheckResult> CheckAsync(StubHealthSource source) =>
        await new TelemetryWriterLivenessHealthCheck(source)
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

    [Fact]
    public async Task ARunningDrainLoopIsHealthy()
    {
        HealthCheckResult result = await CheckAsync(new StubHealthSource { IsDraining = true });

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    /// <summary>
    /// The fault this check exists for, and the only persistence problem a restart cures.
    /// </summary>
    [Fact]
    public async Task AStoppedDrainLoopIsUnhealthy()
    {
        HealthCheckResult result = await CheckAsync(new StubHealthSource { IsDraining = false });

        result.Status.Should().Be(HealthStatus.Unhealthy,
            "the service would go on serving while never persisting anything again");
    }

    /// <summary>
    /// The trap this split exists to avoid: a database rejecting writes must not reach a liveness
    /// probe.
    /// </summary>
    /// <remarks>
    /// Restarting does not fix a broken database, and it discards every sample and event still
    /// buffered - so a liveness probe wired to persistence health would convert a recoverable outage
    /// into guaranteed data loss, once per restart cycle. Only the loop's own state may count here.
    /// </remarks>
    [Fact]
    public async Task AFailingDatabaseDoesNotFailLiveness()
    {
        StubHealthSource source = new()
        {
            IsDraining = true,
            Current = new TelemetryHealthSnapshot(
                DateTimeOffset.UtcNow.AddHours(-1),
                ConsecutiveFailures: 500,
                PendingSamples: 10_000,
                PendingEvents: 5_000,
                DroppedMessages: 0,
                DiscardedEvents: 900),
        };

        HealthCheckResult result = await CheckAsync(source);

        result.Status.Should().Be(HealthStatus.Healthy,
            "a restart would not fix the database and would throw away everything still buffered");
    }
}
