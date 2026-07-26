using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Homespool.Host.Services;

/// <summary>
/// Turns a <see cref="HealthReport"/> into banner items.
/// </summary>
/// <remarks>
/// <para>
/// Reads the report rather than re-deriving anything from
/// <see cref="PrusaConnect.ITelemetryHealthSource"/> directly, so the banner and <c>/health</c> can
/// never disagree about what counts as a problem or how to describe it. Adding a health check is
/// enough to make it appear here too.
/// </para>
/// <para>
/// This exists because the failure that motivated it was invisible from the UI: the service kept
/// serving pages and holding printer connections while persisting nothing, and the only evidence was
/// an Error line in a log nobody was reading. A monitoring system covers that for deployments which
/// have one; this covers the ones that do not, which for a self-hosted workshop service is most of
/// them.
/// </para>
/// </remarks>
public static class HealthBanner
{
    public static IReadOnlyList<HealthBannerItem> From(HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        // Each item's text is the check's own description, not a rewording of it. Those are already
        // written as sentences for whoever gets paged, and a second phrasing here would drift.
        return report.Entries
            .Where(entry => entry.Value.Status != HealthStatus.Healthy)
            .Select(entry => new HealthBannerItem(
                entry.Value.Description ?? $"{entry.Key} is {entry.Value.Status}.",
                entry.Value.Status == HealthStatus.Unhealthy ? "alert-danger" : "alert-warning"))
            .ToList();
    }
}
