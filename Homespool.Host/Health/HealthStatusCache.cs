using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Homespool.Host.Health;

/// <summary>
/// The overall health status an anonymous caller of <c>/health</c> is told, computed at most once
/// every <see cref="Lifetime"/> however many callers ask.
/// </summary>
/// <remarks>
/// <para>
/// <b>The checks are not free, and the caller is nobody.</b> One report costs DNS resolutions of the
/// configured printer host and, with a printer on the plaintext listener, a database query. Anybody
/// who can reach the port could otherwise turn that into load on the resolver and the database at
/// whatever rate they like. A probe polls every few seconds at most, so a status this old is one it
/// would have been given anyway.
/// </para>
/// <para>
/// <b>Concurrent callers share one computation</b>, not merely one result: a burst arriving while the
/// cache is empty would otherwise start a report each before the first one finished. The computation
/// is therefore not tied to any caller's request, since the first caller going away must not cancel
/// it for the rest; each caller abandons only its own wait.
/// </para>
/// <para>
/// An administrator's report is never served from here - see <see cref="HealthEndpoints"/>.
/// </para>
/// </remarks>
public sealed class HealthStatusCache
{
    /// <summary>How long a computed status is served before the checks run again.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(10);

    private readonly HealthCheckService _checks;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    private Task<HealthStatus>? _current;
    private DateTimeOffset _completedAt;

    public HealthStatusCache(HealthCheckService checks, TimeProvider time)
    {
        _checks = checks;
        _time = time;
    }

    /// <summary>
    /// The status from a report no older than <see cref="Lifetime"/>, starting one if there is none.
    /// </summary>
    /// <param name="cancellationToken">Abandons this caller's wait; never the shared computation.</param>
    public Task<HealthStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        Task<HealthStatus> current;

        lock (_gate)
        {
            if (_current is null || IsSpent(_current))
            {
                _current = ComputeAsync();
            }

            current = _current;
        }

        return current.WaitAsync(cancellationToken);
    }

    /// <summary>Whether a computation has finished and may no longer be served.</summary>
    /// <remarks>Called under <see cref="_gate"/>, which is also what <see cref="_completedAt"/> is
    /// written under - a completed task has always had its completion time recorded.</remarks>
    private bool IsSpent(Task<HealthStatus> computation)
    {
        if (!computation.IsCompleted)
        {
            return false;
        }

        // A failure is never cached: the next caller should get a fresh attempt, not the exception.
        return !computation.IsCompletedSuccessfully || _time.GetUtcNow() - _completedAt >= Lifetime;
    }

    private async Task<HealthStatus> ComputeAsync()
    {
        HealthReport report = await _checks.CheckHealthAsync(CancellationToken.None);

        lock (_gate)
        {
            _completedAt = _time.GetUtcNow();
        }

        return report.Status;
    }
}
