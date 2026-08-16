using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Homespool.Host.Telemetry;

/// <summary>
/// The only persistence fault worth restarting for: the drain loop itself has stopped.
/// </summary>
/// <remarks>
/// <para>
/// Split out from <see cref="TelemetryPersistenceHealthCheck"/> because a liveness probe must fail
/// only for conditions a restart actually fixes. Persistence health is the opposite of that - a
/// database rejecting writes is not cured by killing the process, and killing it discards every
/// sample and event still buffered. Wiring a liveness probe to that check would turn a recoverable
/// outage into guaranteed data loss on every restart cycle, which is exactly the trap the obvious
/// configuration leads to.
/// </para>
/// <para>
/// A stopped drain loop is the genuine article: the service goes on serving pages and holding its
/// printer connections while nothing is written again, forever, and a restart really does fix it.
/// </para>
/// </remarks>
public sealed class TelemetryWriterLivenessHealthCheck : IHealthCheck
{
    private readonly ITelemetryHealthSource _source;

    public TelemetryWriterLivenessHealthCheck(ITelemetryHealthSource source)
    {
        _source = source;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_source.IsDraining)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                                       "The telemetry drain loop has stopped; nothing will be persisted until the process restarts."));
        }

        return Task.FromResult(HealthCheckResult.Healthy("The telemetry drain loop is running."));
    }
}
