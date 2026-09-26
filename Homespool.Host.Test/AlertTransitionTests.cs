using System.Collections.Generic;
using System.Linq;

using AwesomeAssertions;

using Microsoft.Extensions.Diagnostics.HealthChecks;

using Homespool.Host.Health;

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
    private const string Database = "telemetry-persistence";
    private const string Mail = "mail";
    private const string Update = "update-check";

    private static Dictionary<string, HealthStatus> Checks(params (string name, HealthStatus status)[] checks)
    {
        return checks.ToDictionary(c => c.name, c => c.status);
    }

    private static HashSet<string> Incident(params string[] names)
    {
        return [.. names];
    }

    [Fact]
    public void BecomingUnhealthyAlerts()
    {
        AlertDecision decision = AlertTransition.Decide(Checks((Database, HealthStatus.Unhealthy)), Incident());

        decision.Action.Should().Be(AlertAction.Alert);
        decision.Incident.Should().BeEquivalentTo([Database]);
    }

    [Fact]
    public void StayingUnhealthySendsNothingFurther()
    {
        AlertDecision decision = AlertTransition.Decide(Checks((Database, HealthStatus.Unhealthy)), Incident(Database));

        decision.Action.Should().Be(AlertAction.None, "the incident has already been reported once");
        decision.Incident.Should().BeEquivalentTo([Database]);
    }

    [Fact]
    public void RecoveringAfterAnAlertSendsTheAllClear()
    {
        AlertDecision decision = AlertTransition.Decide(Checks((Database, HealthStatus.Healthy)), Incident(Database));

        decision.Action.Should().Be(AlertAction.Recovered);
        decision.Incident.Should().BeEmpty();
    }

    [Fact]
    public void StayingHealthySendsNothing()
    {
        AlertTransition.Decide(Checks((Database, HealthStatus.Healthy)), Incident())
                       .Action.Should().Be(AlertAction.None);
    }

    /// <summary>
    /// Degraded is a database briefly refusing writes with everything still buffered - common,
    /// self-resolving, and already visible on the banner and /health.
    /// </summary>
    [Fact]
    public void DegradedDoesNotAlert()
    {
        AlertDecision decision = AlertTransition.Decide(Checks((Database, HealthStatus.Degraded)), Incident());

        decision.Action.Should().Be(AlertAction.None);
        decision.Incident.Should().BeEmpty("a Degraded check never starts an incident");
    }

    /// <summary>
    /// Nor does it end an incident: still failing is not recovered, and an all-clear sent while
    /// writes are still failing would be worse than saying nothing.
    /// </summary>
    [Fact]
    public void DegradedDoesNotClearAnExistingAlert()
    {
        AlertDecision decision = AlertTransition.Decide(Checks((Database, HealthStatus.Degraded)), Incident(Database));

        decision.Action.Should().Be(AlertAction.None);
        decision.Incident.Should().BeEquivalentTo([Database]);
    }

    /// <summary>
    /// The case this rule exists for: the database recovers while an image update worth pulling keeps
    /// the overall status Degraded for days. The all-clear is about the database, and goes.
    /// </summary>
    [Fact]
    public void ADegradedCheckOutsideTheIncidentDoesNotHoldBackTheAllClear()
    {
        AlertDecision decision = AlertTransition.Decide(
            Checks((Database, HealthStatus.Healthy), (Update, HealthStatus.Degraded)),
            Incident(Database));

        decision.Action.Should().Be(AlertAction.Recovered);
    }

    /// <summary>
    /// And a check already Degraded when the alert went out is not part of what was reported.
    /// </summary>
    [Fact]
    public void ADegradedCheckIsNotPartOfTheIncidentItWasBesides()
    {
        AlertTransition.Decide(Checks((Database, HealthStatus.Unhealthy), (Update, HealthStatus.Degraded)), Incident())
                       .Incident.Should().BeEquivalentTo([Database]);
    }

    /// <summary>
    /// A second check failing while the first is still out joins the incident rather than sending a
    /// second alert, and the all-clear waits for both.
    /// </summary>
    [Fact]
    public void ACheckFailingDuringTheIncidentJoinsIt()
    {
        AlertDecision joined = AlertTransition.Decide(
            Checks((Database, HealthStatus.Unhealthy), (Mail, HealthStatus.Unhealthy)),
            Incident(Database));

        joined.Action.Should().Be(AlertAction.None, "the incident is already reported");
        joined.Incident.Should().BeEquivalentTo([Database, Mail]);

        AlertTransition.Decide(Checks((Database, HealthStatus.Healthy), (Mail, HealthStatus.Unhealthy)), joined.Incident)
                       .Action.Should().Be(AlertAction.None, "mail is still out");

        AlertTransition.Decide(Checks((Database, HealthStatus.Healthy), (Mail, HealthStatus.Healthy)), joined.Incident)
                       .Action.Should().Be(AlertAction.Recovered);
    }

    [Fact]
    public void ACheckNoLongerInTheReportDoesNotHoldAnIncidentOpen()
    {
        AlertTransition.Decide(Checks((Mail, HealthStatus.Healthy)), Incident(Database))
                       .Action.Should().Be(AlertAction.Recovered);
    }
}
