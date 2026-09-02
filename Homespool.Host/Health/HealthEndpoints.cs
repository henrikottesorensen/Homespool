using System;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

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
                .AddCheck<WebRtcCandidateHealthCheck>("camera-live-view");

        return services;
    }

    /// <summary>
    /// Maps the everything-endpoint and the liveness endpoint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Anonymous by design</b>: a monitoring system holds no credentials, and the response carries
    /// only counters and timestamps about this service's own write path - nothing about printers, jobs
    /// or users.
    /// </para>
    /// <para>
    /// <see cref="HealthEndpointPath"/> is everything, for monitoring and for humans. Alert on it;
    /// never restart on it. <c>/health/live</c> is the safe target for anything that can kill the
    /// container - a Kubernetes livenessProbe, a Swarm healthcheck, an autoheal sidecar - because it
    /// reports only faults a restart fixes, so a rejecting database can never trigger a restart loop
    /// that discards the buffered telemetry with every cycle.
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

        app.MapHealthChecks(HealthEndpointPath, new HealthCheckOptions
        {
            ResponseWriter = WriteHealthResponseAsync,
        }).SegregateByListener();

        app.MapHealthChecks($"{HealthEndpointPath}/live", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(LivenessTag),
            ResponseWriter = WriteHealthResponseAsync,
        }).SegregateByListener();
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
