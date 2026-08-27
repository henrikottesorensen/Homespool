using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using Homespool.Data;

namespace Homespool.Host.Cameras;

/// <summary>
/// Reports a deployment whose camera sidecar has no credential, and therefore no cameras.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because the failure is otherwise silent to the one person who can fix it.</b>
/// Without a credential <see cref="Go2RtcClient"/> refuses every call, so an existing deployment
/// that upgrades into this rule does not see an error - it sees cameras that used to work and now
/// show nothing, which reads as a broken camera rather than a missing setting.
/// <see cref="Health.HealthBanner"/> turns any unhealthy check into an administrator's banner, so
/// answering here is the whole of that job.
/// </para>
/// <para>
/// <b>Degraded, never Unhealthy</b>, following <see cref="Health.DeploymentExposureHealthCheck"/>:
/// Unhealthy makes <c>/health</c> a 503 that reads as "the service is down" to whatever is watching,
/// and this is a judgement about configuration. Deliberately untagged, so it can never reach
/// <c>/health/live</c> and drive a restart loop - a restart would faithfully reproduce it.
/// </para>
/// <para>
/// <b>Healthy when no camera is configured</b>, and that is the point of asking the database rather
/// than the options alone. Most deployments have no camera, the sidecar idles, and telling them
/// about a credential they have no use for is how a banner trains people to ignore banners.
/// </para>
/// </remarks>
public sealed class CameraCredentialHealthCheck : IHealthCheck
{
    private readonly IOptions<CameraOptions> _options;
    private readonly HomespoolDbContext _dbContext;

    public CameraCredentialHealthCheck(IOptions<CameraOptions> options, HomespoolDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _dbContext = dbContext;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
                                                          CancellationToken cancellationToken = default)
    {
        if (_options.Value.IsAuthenticated)
        {
            // Checked before the happy answer, because this state looks exactly like health from
            // every other angle: the credential is set, this process holds it, and only the sidecar
            // disagrees - by answering 401 to everything, with nothing saying why.
            if (!_options.Value.CredentialSurvivesTransport)
            {
                return HealthCheckResult.Degraded(
                    "The camera stream server's credential contains a double quote or a backslash, which cannot "
                    + "survive the JSON command line it is passed on - the sidecar receives a different value than "
                    + "this process does, so every camera will answer 401 while both halves look correctly "
                    + "configured. Regenerate it with `openssl rand -base64 24`, whose output contains neither.");
            }

            return HealthCheckResult.Healthy("The camera stream server has a credential.");
        }

        int cameras = await _dbContext.Cameras.CountAsync(cancellationToken).ConfigureAwait(false);

        if (cameras == 0)
        {
            return HealthCheckResult.Healthy(
                "No cameras are configured, and the camera stream server has no credential. Setting one is only "
                + "needed before adding a camera.");
        }

        return HealthCheckResult.Degraded(
            $"{cameras} camera(s) are configured but the stream server has no credential, so none of them will "
            + "produce a picture. Set GO2RTC_USERNAME and GO2RTC_PASSWORD in .env - ./setup-env.sh generates them - "
            + "and restart. Without a credential the sidecar's own API is open to anything that can reach it, "
            + "which is why Homespool declines to use it rather than using it unauthenticated.");
    }
}
