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
/// <b>Reported whether or not a camera is configured.</b> A deployment cannot leave this state by
/// itself: adding a camera is refused while there is no credential, so the camera count that would
/// justify speaking up can never rise, and staying quiet until it does means staying quiet forever
/// on a deployment that never ran the setup script. The count is still asked for, because it changes
/// what there is to say - cameras that will not work is a different sentence from cameras that
/// cannot be added.
/// </para>
/// </remarks>
public sealed class CameraCredentialHealthCheck : IHealthCheck
{
    private readonly IOptionsMonitor<CameraOptions> _options;
    private readonly HomespoolDbContext _dbContext;

    public CameraCredentialHealthCheck(IOptionsMonitor<CameraOptions> options, HomespoolDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _dbContext = dbContext;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
                                                          CancellationToken cancellationToken = default)
    {
        if (_options.CurrentValue.IsAuthenticated)
        {
            // Checked before the happy answer, because this state looks exactly like health from
            // every other angle: the credential is set, this process holds it, and only the sidecar
            // disagrees - by answering 401 to everything, with nothing saying why.
            if (!_options.CurrentValue.CredentialSurvivesTransport)
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

        // Not "the sidecar's API is open to anything that can reach it": the shipped stack starts it
        // with no API at all until the credential exists, so the cost of this state is that cameras
        // do not work rather than that something is exposed.
        const string Remedy =
            "Set GO2RTC_USERNAME and GO2RTC_PASSWORD in .env - ./setup-env.sh generates them - and restart. "
            + "Until then the stream server starts with its API switched off, so nothing can drive it, and "
            + "Homespool declines to use an unauthenticated one in any case.";

        return HealthCheckResult.Degraded(
            cameras == 0 ?
                $"The camera stream server has no credential, so no camera can be added. {Remedy}" :
                $"{cameras} camera(s) are configured but the stream server has no credential, so none of them "
                + $"will produce a picture. {Remedy}");
    }
}
