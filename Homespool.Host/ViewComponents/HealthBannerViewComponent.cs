using System.Collections.Generic;
using System.Threading.Tasks;

using Homespool.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Homespool.Host.ViewComponents;

/// <summary>
/// Shows any unhealthy health check as a banner, on every page, to administrators.
/// </summary>
/// <remarks>
/// <para>
/// Administrators only: a stuck telemetry writer is an operational problem an ordinary user can
/// neither act on nor interpret, and printing is unaffected by it. Widening this is one line, if the
/// people who use the app are the same people who run it.
/// </para>
/// <para>
/// <b>One item does not come from the report, and cannot.</b> Whether the administrator reading this
/// is doing so over plain HTTP is a property of <i>their request</i> - this server has no user-facing
/// address to inspect at startup, deliberately, since links in mail come from whatever the request
/// said. A health check has no request; this does. So the exposure rule is asked here, and the banner
/// stops being purely a rendering of the health report - which its own remarks used to promise.
/// </para>
/// <para>
/// Running the checks per render is cheap by construction - they read in-memory counters, no
/// database round trip - and this deliberately does not cache. A banner that lags the problem by a
/// polling interval would be worse than none while someone is actively looking at whether the thing
/// they just fixed took effect.
/// </para>
/// </remarks>
public sealed class HealthBannerViewComponent : ViewComponent
{
    private readonly HealthCheckService _healthChecks;

    public HealthBannerViewComponent(HealthCheckService healthChecks)
    {
        _healthChecks = healthChecks;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (!UserClaimsPrincipal.IsInRole(Services.AdminBootstrap.AdminRole))
        {
            return View(new List<HealthBannerItem>());
        }

        HealthReport report = await _healthChecks.CheckHealthAsync(HttpContext.RequestAborted);

        List<HealthBannerItem> items = [.. HealthBanner.From(report)];

        ExposureVerdict session = DeploymentExposure.EvaluateAdminSession(
            Request.IsHttps, HttpContext.Connection.RemoteIpAddress);

        if (session.IsProblem)
        {
            // First, and in the loudest style available: everything else on this page is being read
            // over the same connection, including whatever the reader is about to do about it.
            items.Insert(0, new HealthBannerItem(session.Description, "alert-danger", "Insecure connection:"));
        }

        return View(items);
    }
}
