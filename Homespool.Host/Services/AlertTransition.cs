using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Homespool.Host.Services;

/// <summary>What, if anything, to send about the current health status.</summary>
public enum AlertAction
{
    /// <summary>Nothing has changed that an administrator needs told.</summary>
    None,

    /// <summary>Newly broken. Send once.</summary>
    Alert,

    /// <summary>Recovered from a state we alerted about. Send the all-clear once.</summary>
    Recovered,
}

/// <summary>
/// Decides when a health status change is worth an email, given whether one has already been sent.
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
/// still-failing is not recovered. Only a return to Healthy ends an incident.
/// </para>
/// </remarks>
public static class AlertTransition
{
    public static AlertAction Decide(HealthStatus status, bool alreadyAlerted)
    {
        return status switch
        {
            HealthStatus.Unhealthy when !alreadyAlerted => AlertAction.Alert,
            HealthStatus.Healthy when alreadyAlerted => AlertAction.Recovered,
            _ => AlertAction.None,
        };
    }
}
