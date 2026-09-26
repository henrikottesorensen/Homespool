using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Homespool.Host.Health;

/// <summary>What, if anything, to send about the current health status.</summary>
public enum AlertAction
{
    Undefined = 0,

    /// <summary>Nothing has changed that an administrator needs told.</summary>
    None = 1,

    /// <summary>Newly broken. Send once.</summary>
    Alert = 2,

    /// <summary>Recovered from a state we alerted about. Send the all-clear once.</summary>
    Recovered = 3,
}

/// <summary>
/// Decides when a health status change is worth an email, given the incident already reported.
/// </summary>
/// <remarks>
/// <para>
/// Separated from the service that sends so the rule can be tested without a mail server, a
/// database or a clock - the rule is the part that matters, since getting it wrong means either
/// silence or a mailbox full of identical alerts. The incident that prompted all of this produced
/// 176 identical failures in six minutes; this must send one message.
/// </para>
/// <para>
/// Degraded deliberately does neither. It is a database briefly refusing writes with everything
/// still buffered and nothing lost - common, self-resolving, and not worth waking anyone for; the
/// banner and <c>/health</c> already show it. Nor does Degraded clear a previous alert, because
/// still-failing is not recovered.
/// </para>
/// <para>
/// <b>An incident is the checks that caused it, not the overall status.</b> The alert remembers which
/// checks were Unhealthy, a check that turns Unhealthy while it is open joins it, and the all-clear
/// goes once every one of them is Healthy again. A check that was only ever Degraded never joins, so
/// it cannot hold the all-clear back: an image update worth pulling, a missing camera credential -
/// Degraded for days, and nothing to do with the database that failed. Keyed on the overall status,
/// any of those would have kept the recovery of an unrelated fault from ever being announced.
/// </para>
/// </remarks>
public static class AlertTransition
{
    private static readonly IReadOnlySet<string> NoIncident = FrozenSet<string>.Empty;

    /// <summary>Decides what, if anything, to send.</summary>
    /// <param name="checks">Every check in the current report, by name.</param>
    /// <param name="incident">The checks of the incident already reported; empty when there is none.</param>
    /// <returns>What to send, and the incident as it now stands.</returns>
    public static AlertDecision Decide(IReadOnlyDictionary<string, HealthStatus> checks,
                                       IReadOnlySet<string> incident)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(incident);

        HashSet<string> unhealthy = [.. checks.Where(c => c.Value == HealthStatus.Unhealthy).Select(c => c.Key)];

        if (incident.Count == 0)
        {
            return unhealthy.Count > 0 ?
                new AlertDecision(AlertAction.Alert, unhealthy) :
                new AlertDecision(AlertAction.None, NoIncident);
        }

        HashSet<string> open = [.. incident, .. unhealthy];

        // A check missing from the report cannot say it is still failing, and a check is only
        // missing when the set of registered checks changed; neither keeps an incident open.
        bool cleared = open.All(name => !checks.TryGetValue(name, out HealthStatus status) || status == HealthStatus.Healthy);

        return cleared ?
            new AlertDecision(AlertAction.Recovered, NoIncident) :
            new AlertDecision(AlertAction.None, open);
    }
}

/// <summary>What to send, and which checks the open incident now consists of.</summary>
/// <param name="Action">The email to send, if any.</param>
/// <param name="Incident">
/// The checks that were Unhealthy while the incident was open - empty when there is none. Kept only
/// once an alert has actually been sent, so a send that failed is tried again.
/// </param>
public readonly record struct AlertDecision(AlertAction Action, IReadOnlySet<string> Incident);
