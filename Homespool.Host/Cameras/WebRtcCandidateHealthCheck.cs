using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Cameras;

/// <summary>
/// Reports a deployment that has cameras but no address to watch them live from.
/// </summary>
/// <remarks>
/// <para>
/// <b>The absence has no symptom, which is the whole reason this exists.</b> Without a candidate the
/// live-view button simply never appears, and a missing button looks exactly like a feature that was
/// never built. Nothing is broken, nothing is logged repeatedly, and the person who could fix it has
/// no reason to suspect there is anything to fix.
/// </para>
/// <para>
/// <b>Degraded, never Unhealthy, and untagged</b>, following <see cref="CameraCredentialHealthCheck"/>:
/// this is a judgement about configuration, stills are unaffected, and a restart would faithfully
/// reproduce it — so it must not reach <c>/health/live</c> and drive a restart loop.
/// </para>
/// <para>
/// <b>Quiet when there are no cameras</b>, and the same reasoning as its neighbour: most deployments
/// have none, and telling them about an address they have no use for is how a banner teaches people
/// to ignore banners.
/// </para>
/// <para>
/// <b>It reports rather than re-derives.</b> The address is worked out once by
/// <see cref="WebRtcConfigurer"/> and read here through <see cref="CameraLiveAvailability"/>, so what the
/// banner says and what the sidecar was told cannot drift apart — a second computation could reach a
/// different answer, since it depends on a name resolving.
/// </para>
/// </remarks>
public sealed class WebRtcCandidateHealthCheck : IHealthCheck
{
    private readonly CameraLiveAvailability _availability;
    private readonly IOptions<CameraOptions> _cameras;
    private readonly IOptions<PrusaConnectOptions> _connect;
    private readonly HomespoolDbContext _dbContext;

    public WebRtcCandidateHealthCheck(CameraLiveAvailability availability,
                                      IOptions<CameraOptions> cameras,
                                      IOptions<PrusaConnectOptions> connect,
                                      HomespoolDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(cameras);
        ArgumentNullException.ThrowIfNull(connect);

        _availability = availability;
        _cameras = cameras;
        _connect = connect;
        _dbContext = dbContext;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
                                                          CancellationToken cancellationToken = default)
    {
        if (_availability.IsConfigured)
        {
            return HealthCheckResult.Healthy(
                $"Cameras can be watched live; browsers are told to use {_availability.Candidate}.");
        }

        // Asked second, because a deployment with no cameras has nothing to be told and this is the
        // only question that costs a query.
        int cameras = await _dbContext.Cameras.CountAsync(cancellationToken).ConfigureAwait(false);

        if (cameras == 0)
        {
            return HealthCheckResult.Healthy(
                "No cameras are configured, so no address is needed for live view.");
        }

        // Two different faults with two different fixes, and saying which is most of the value here:
        // an operator who set nothing needs to know a name is missing, and one who set a name that
        // resolves to nothing useful would otherwise read the same sentence and check the same
        // setting twice.
        string remedy = _cameras.Value.WebRtcCandidate.Length > 0
            ? "WEBRTC_CANDIDATE is set but was not usable - it should be an address and port a browser can reach, "
              + "such as 192.168.1.10:8555, with no scheme."
            : _connect.Value.IsPrinterAddressConfigured
                ? $"PRINTER_HOST is set to '{_connect.Value.PrinterHost.Trim()}' but does not resolve to an address "
                  + "outside this deployment's own container networks. Set WEBRTC_CANDIDATE to the address and port "
                  + "a browser should use."
                : "Set PRINTER_HOST to a name this machine answers to, which is what the address is worked out from, "
                  + "or set WEBRTC_CANDIDATE to the address and port a browser should use.";

        return HealthCheckResult.Degraded(
            $"{cameras} camera(s) are configured but none can be watched live, because there is no address to send "
            + $"video to. Still pictures are unaffected and keep working. {remedy}");
    }
}
