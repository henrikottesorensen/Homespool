using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using PrinterService.Host.Services;

namespace PrinterService.Host.ViewComponents;

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

        return View(HealthBanner.From(report));
    }
}
