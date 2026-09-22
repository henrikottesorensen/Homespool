using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using Homespool.Host.Authorisation;
using Homespool.Host.Health;

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
/// <b>Running the checks per render is not free, and this deliberately does not cache.</b> Every page
/// an administrator loads resolves the configured printer host (the certificate and exposure checks
/// each do) and, with a printer on the plaintext listener, queries the database. That is accepted
/// because only administrators pay it, and a banner that lags the problem by a polling interval would
/// be worse than none while someone is actively looking at whether the thing they just fixed took
/// effect. The anonymous status on <c>/health</c> is the one that is cached, for the opposite reason:
/// its callers are nobody in particular.
/// </para>
/// </remarks>
public sealed class HealthBannerViewComponent : ViewComponent
{
    private readonly HealthCheckService _healthChecks;
    private readonly IAuthorizationService _authorization;

    public HealthBannerViewComponent(HealthCheckService healthChecks, IAuthorizationService authorization)
    {
        _healthChecks = healthChecks;
        _authorization = authorization;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (!(await _authorization.AuthorizeAsync(UserClaimsPrincipal, Policies.Administrator)).Succeeded)
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
            items.Insert(0, new HealthBannerItem(session.Description, "alert-danger", "Health_InsecureConnection"));
        }

        return View(items);
    }
}
