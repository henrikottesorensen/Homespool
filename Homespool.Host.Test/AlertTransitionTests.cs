using AwesomeAssertions;

using Microsoft.Extensions.Diagnostics.HealthChecks;

using Homespool.Host.Services;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="AlertTransition"/> - when a health status is worth an email.
/// </summary>
/// <remarks>
/// The rule is the whole feature. Get it wrong in one direction and an incident passes in silence;
/// wrong in the other and the flush bug that prompted this would have sent 176 identical emails in
/// six minutes.
/// </remarks>
public class AlertTransitionTests
{
    [Fact]
    public void BecomingUnhealthyAlerts()
    {
        AlertTransition.Decide(HealthStatus.Unhealthy, alreadyAlerted: false)
            .Should().Be(AlertAction.Alert);
    }

    [Fact]
    public void StayingUnhealthySendsNothingFurther()
    {
        AlertTransition.Decide(HealthStatus.Unhealthy, alreadyAlerted: true)
            .Should().Be(AlertAction.None, "the incident has already been reported once");
    }

    [Fact]
    public void RecoveringAfterAnAlertSendsTheAllClear()
    {
        AlertTransition.Decide(HealthStatus.Healthy, alreadyAlerted: true)
            .Should().Be(AlertAction.Recovered);
    }

    [Fact]
    public void StayingHealthySendsNothing()
    {
        AlertTransition.Decide(HealthStatus.Healthy, alreadyAlerted: false)
            .Should().Be(AlertAction.None);
    }

    /// <summary>
    /// Degraded is a database briefly refusing writes with everything still buffered - common,
    /// self-resolving, and already visible on the banner and /health.
    /// </summary>
    [Fact]
    public void DegradedDoesNotAlert()
    {
        AlertTransition.Decide(HealthStatus.Degraded, alreadyAlerted: false)
            .Should().Be(AlertAction.None);
    }

    /// <summary>
    /// Nor does it end an incident: still failing is not recovered, and an all-clear sent while
    /// writes are still failing would be worse than saying nothing.
    /// </summary>
    [Fact]
    public void DegradedDoesNotClearAnExistingAlert()
    {
        AlertTransition.Decide(HealthStatus.Degraded, alreadyAlerted: true)
            .Should().Be(AlertAction.None);
    }
}
