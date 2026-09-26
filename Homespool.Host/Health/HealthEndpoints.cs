using System;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using Homespool.Host.Authorisation;
using Homespool.Host.Cameras;
using Homespool.Host.Certificates;
using Homespool.Host.Listeners;
using Homespool.Host.Telemetry;

namespace Homespool.Host.Health;

/// <summary>
/// The health checks and the two endpoints that report them.
/// </summary>
/// <remarks>
/// Both halves live here rather than in a registration file and a mapping file, because
/// <see cref="LivenessTag"/> is the only thing keeping the two endpoints from reporting the same
/// thing: it is written on a check in one method and filtered on in the other, and splitting them
/// would let those drift apart silently.
/// </remarks>
public static class HealthEndpoints
{
    /// <summary>Where the health endpoints live. Shared so the HTTPS-redirection exclusion and the
    /// setup gate's allowance cannot drift away from the routes themselves. <c>/health/live</c> sits
    /// underneath, so both are covered by one path prefix.</summary>
    public const string HealthEndpointPath = "/health";

    /// <summary>Marks a check as safe for a liveness probe - that is, one whose failure a restart
    /// would actually fix.</summary>
    private const string LivenessTag = "live";

    /// <summary>
    /// Adds every health check, tagging the ones a restart would actually fix.
    /// </summary>
    /// <remarks>
    /// The process answering requests says nothing about whether it is still recording anything - a
    /// flush bug once made every write fail permanently while the service looked entirely healthy
    /// from outside. This is the hook a monitoring system can watch.
    /// </remarks>
    public static IServiceCollection AddHomespoolHealthChecks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Tagged, because the two endpoints below must not report the same thing. Only checks
        // tagged "live" answer /health/live, and only a fault a restart would fix may carry that
        // tag - see TelemetryWriterLivenessHealthCheck.
        services.AddHealthChecks()
                .AddCheck<TelemetryPersistenceHealthCheck>("telemetry-persistence")
                .AddCheck<TelemetryWriterLivenessHealthCheck>("telemetry-writer-alive", tags: [LivenessTag])

                // Deliberately untagged: a certificate that no longer matches this machine is not a
                // fault a restart fixes, and the banner picks it up from the report either way.
                .AddCheck<PrinterCertificateHealthCheck>("printer-certificate")

                // Also untagged: a deployment handing tokens to the internet is misconfigured, not
                // broken, and a restart would faithfully reproduce it.
                .AddCheck<DeploymentExposureHealthCheck>("deployment-exposure")

                // Untagged for the same reason. Cameras stop working entirely without a sidecar
                // credential, and the person who can fix that otherwise sees only blank cameras.
                .AddCheck<CameraCredentialHealthCheck>("camera-credential")

                // Untagged again, and the quietest failure of the three: with no address to send
                // video to, the live-view button simply never appears, which is indistinguishable
                // from a feature that was never built.
                .AddCheck<WebRtcCandidateHealthCheck>("camera-live-view")

                // Untagged: an image worth pulling is not a fault, and a restart pulls nothing. The
                // check keeps the last report it read, so it is the one singleton among them.
                .AddCheck<UpdateReportHealthCheck>("update-check");

        services.AddSingleton<UpdateReportHealthCheck>();
        services.AddSingleton<HealthStatusCache>();

        return services;
    }

    /// <summary>
    /// Maps the everything-endpoint and the liveness endpoint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Anonymous, but only the status is.</b> A monitoring system holds no credentials, so both
    /// endpoints are mapped outside authentication - and a monitor alerts on the status code, which is
    /// all it needs. The report behind it is for the people who run the deployment: a check describes
    /// what an operator would need in order to act, which across the seven here means the configured
    /// printer host and the addresses it resolves to, the names the printer certificate covers against
    /// the names this machine now answers to, the WebRTC candidate browsers are handed, sentences
    /// saying which part of the deployment is exposed and how, and whether it is running behind a
    /// published fix. That is a map of the network and a list of its weak points, and anybody on the
    /// same network can ask. So <see cref="HealthEndpointPath"/>
    /// answers an administrator's cookie with the whole report and everybody else with the overall
    /// status alone - not even each check's own status, since which check is failing is itself a
    /// description. The administrator test is the banner's, which already shows the same descriptions
    /// to the same people.
    /// </para>
    /// <para>
    /// The proxy additionally admits private ranges only, in
    /// <c>nginx/homespool-health-access.conf</c>. That is no longer what keeps the report private;
    /// it keeps the endpoint off the internet.
    /// </para>
    /// <para>
    /// <see cref="HealthEndpointPath"/> is everything, for monitoring and for humans. Alert on it;
    /// never restart on it. <c>/health/live</c> is the safe target for anything that can kill the
    /// container - a Kubernetes livenessProbe, a Swarm healthcheck, an autoheal sidecar - because it
    /// reports only faults a restart fixes, so a rejecting database can never trigger a restart loop
    /// that discards the buffered telemetry with every cycle. Its body is the liveness check's fixed
    /// sentence, which says nothing about the deployment, so it stays whole for everyone.
    /// </para>
    /// <para>
    /// <c>/health/live</c> is also the right target for a startupProbe: migrations and admin bootstrap
    /// run before <c>app.Run()</c>, so Kestrel is not accepting connections until they finish - any
    /// successful response already means startup completed, and no separate endpoint is needed. And
    /// for a readinessProbe, since a degraded writer is a reason to alert, not a reason to stop
    /// accepting printer connections.
    /// </para>
    /// </remarks>
    public static void MapHomespoolHealthChecks(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Map rather than MapGet, as MapHealthChecks does: a probe may well send HEAD.
        app.Map(HealthEndpointPath, ServeHealthAsync).SegregateByListener();

        app.MapHealthChecks($"{HealthEndpointPath}/live", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(LivenessTag),
            ResponseWriter = WriteHealthResponseAsync,
        }).SegregateByListener();
    }

    /// <summary>
    /// Answers <see cref="HealthEndpointPath"/>: the whole report, fresh, to an administrator, and the
    /// cached overall status to anybody else.
    /// </summary>
    /// <remarks>
    /// Written by hand because <c>MapHealthChecks</c> runs every check before its response writer can
    /// see who is asking, so it can neither skip the work for a cached caller nor share it. What it
    /// does is reproduced: Healthy and Degraded are 200, Unhealthy is 503, and the response is marked
    /// uncacheable, so a proxy never serves one caller's report to another. An administrator is never
    /// served the cache - somebody checking whether the thing they just fixed took effect needs the
    /// answer now.
    /// </remarks>
    private static async Task ServeHealthAsync(HttpContext context)
    {
        HealthReport? report = null;
        HealthStatus status;

        IAuthorizationService authorization = context.RequestServices.GetRequiredService<IAuthorizationService>();

        if ((await authorization.AuthorizeAsync(context.User, Policies.Administrator)).Succeeded)
        {
            report = await context.RequestServices.GetRequiredService<HealthCheckService>()
                                  .CheckHealthAsync(context.RequestAborted);
            status = report.Status;
        }
        else
        {
            status = await context.RequestServices.GetRequiredService<HealthStatusCache>()
                                  .GetStatusAsync(context.RequestAborted);
        }

        context.Response.StatusCode = status == HealthStatus.Unhealthy ?
            StatusCodes.Status503ServiceUnavailable :
            StatusCodes.Status200OK;

        context.Response.Headers.CacheControl = "no-store, no-cache";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "Thu, 01 Jan 1970 00:00:00 GMT";

        if (report is not null)
        {
            await WriteHealthResponseAsync(context, report);
            return;
        }

        context.Response.ContentType = MediaTypeNames.Application.Json;
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { status = status.ToString() }));
    }

    /// <summary>
    /// Writes the health report as JSON rather than the default bare status word.
    /// </summary>
    /// <remarks>
    /// The status code is what a monitoring system alerts on - Healthy and Degraded are 200,
    /// Unhealthy is 503 - but the body is what tells whoever gets paged which of the two very
    /// different problems they have: a database that is briefly stuck, or one that has been stuck
    /// long enough to lose events for good.
    /// </remarks>
    private static Task WriteHealthResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = MediaTypeNames.Application.Json;

        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                data = entry.Value.Data,
            }),
        }));
    }
}
