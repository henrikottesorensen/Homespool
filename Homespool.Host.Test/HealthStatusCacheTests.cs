using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Homespool.Host.Health;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="HealthStatusCache"/> - how often an anonymous caller can make the checks run.
/// </summary>
public class HealthStatusCacheTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly HealthCheckService _checks = Substitute.For<HealthCheckService>();
    private readonly Queue<Task<HealthReport>> _reports = new();
    private int _runs;

    public HealthStatusCacheTests()
    {
        _checks.CheckHealthAsync(Arg.Any<Func<HealthCheckRegistration, bool>?>(), Arg.Any<CancellationToken>())
               .Returns(_ =>
               {
                   _runs++;

                   return _reports.Dequeue();
               });
    }

    private static HealthReport Report(HealthStatus status)
    {
        return new HealthReport(new Dictionary<string, HealthReportEntry>(), status, TimeSpan.Zero);
    }

    [Fact]
    public async Task AStatusIsServedAgainWithinItsLifetime()
    {
        // Arrange
        HealthStatusCache cache = new(_checks, _time);
        _reports.Enqueue(Task.FromResult(Report(HealthStatus.Degraded)));
        await cache.GetStatusAsync(CancellationToken.None);

        // Act
        _time.Advance(HealthStatusCache.Lifetime - TimeSpan.FromMilliseconds(1));
        HealthStatus status = await cache.GetStatusAsync(CancellationToken.None);

        // Assert
        status.Should().Be(HealthStatus.Degraded);
        _runs.Should().Be(1, "a caller inside the lifetime must not be able to make the checks run");
    }

    [Fact]
    public async Task AnExpiredStatusIsComputedAgain()
    {
        // Arrange
        HealthStatusCache cache = new(_checks, _time);
        _reports.Enqueue(Task.FromResult(Report(HealthStatus.Healthy)));
        _reports.Enqueue(Task.FromResult(Report(HealthStatus.Unhealthy)));
        await cache.GetStatusAsync(CancellationToken.None);

        // Act
        _time.Advance(HealthStatusCache.Lifetime);
        HealthStatus status = await cache.GetStatusAsync(CancellationToken.None);

        // Assert
        status.Should().Be(HealthStatus.Unhealthy, "a probe must learn of a fault within one lifetime");
        _runs.Should().Be(2);
    }

    /// <summary>
    /// A burst arriving before the first report finishes shares it, rather than each starting one.
    /// </summary>
    /// <remarks>
    /// The lifetime is measured from completion, so time passing while a report is still running does
    /// not expire it - which is what this also pins, by advancing past the lifetime mid-computation.
    /// </remarks>
    [Fact]
    public async Task CallersArrivingDuringAComputationShareIt()
    {
        // Arrange
        HealthStatusCache cache = new(_checks, _time);
        TaskCompletionSource<HealthReport> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _reports.Enqueue(pending.Task);

        // Act
        Task<HealthStatus> first = cache.GetStatusAsync(CancellationToken.None);
        _time.Advance(HealthStatusCache.Lifetime * 2);
        Task<HealthStatus> second = cache.GetStatusAsync(CancellationToken.None);
        pending.SetResult(Report(HealthStatus.Degraded));

        // Assert
        (await first).Should().Be(HealthStatus.Degraded);
        (await second).Should().Be(HealthStatus.Degraded);
        _runs.Should().Be(1, "a burst must not start one report per caller");
    }

    /// <summary>
    /// The first caller giving up abandons only its own wait - the others still get the status.
    /// </summary>
    [Fact]
    public async Task ACallerLeavingDoesNotCancelTheComputationForTheRest()
    {
        // Arrange
        HealthStatusCache cache = new(_checks, _time);
        TaskCompletionSource<HealthReport> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _reports.Enqueue(pending.Task);
        using CancellationTokenSource leaving = new();

        Task<HealthStatus> abandoned = cache.GetStatusAsync(leaving.Token);
        Task<HealthStatus> staying = cache.GetStatusAsync(CancellationToken.None);

        // Act
        await leaving.CancelAsync();
        pending.SetResult(Report(HealthStatus.Healthy));

        // Assert
        await FluentActions.Awaiting(() => abandoned).Should().ThrowAsync<OperationCanceledException>();
        (await staying).Should().Be(HealthStatus.Healthy);
        await _checks.Received(1).CheckHealthAsync(Arg.Any<Func<HealthCheckRegistration, bool>?>(), CancellationToken.None);
    }

    [Fact]
    public async Task AFailedComputationIsNotServedAgain()
    {
        // Arrange
        HealthStatusCache cache = new(_checks, _time);
        _reports.Enqueue(Task.FromException<HealthReport>(new InvalidOperationException("the checks threw")));
        _reports.Enqueue(Task.FromResult(Report(HealthStatus.Healthy)));

        await FluentActions.Awaiting(() => cache.GetStatusAsync(CancellationToken.None))
                           .Should().ThrowAsync<InvalidOperationException>();

        // Act - no time passes, so only the failure can be what makes this run again.
        HealthStatus status = await cache.GetStatusAsync(CancellationToken.None);

        // Assert
        status.Should().Be(HealthStatus.Healthy);
        _runs.Should().Be(2);
    }
}
