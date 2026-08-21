using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Cameras;

/// <summary>
/// Adding, changing and removing cameras - and keeping the stream server in step while it happens.
/// </summary>
/// <remarks>
/// <para>
/// <b>The permission split lives here</b> (Henrik, 2026-08-08, calling it the least worst option).
/// A networked camera needs <c>CanManage</c> on the team that will own it: it belongs to whoever
/// can already reach it. A camera plugged into this machine needs <b>administrator</b>, because it
/// is a property of the server rather than of a team - the same category as the printer
/// certificate, and permissioned the same way. <c>CanManage</c> is per-team and there can be
/// several teams, so team permission over one physical device would let two teams claim it with
/// nothing to explain why the second gets nothing.
/// </para>
/// <para>
/// <b>No transaction.</b> One <c>SaveChangesAsync</c> is already transactional, and the other half
/// of this - registering with the stream server - is an HTTP call no rollback would undo. Adding
/// one would buy the appearance of atomicity over an effect that is not atomic. See
/// <c>notes/transactions.md</c>.
/// </para>
/// </remarks>
public class CameraService
{
    private readonly HomespoolDbContext _dbContext;
    private readonly CameraAccessService _access;
    private readonly CameraSourcePolicy _sourcePolicy;
    private readonly Go2RtcClient _streamServer;
    private readonly ICameraSnapshotFetcher _fetcher;
    private readonly CameraFrameCache _frames;
    private readonly CameraLiveAvailability _liveView;
    private readonly LocalCameraDevices _devices;
    private readonly CameraCredentialProtector _credentials;
    private readonly TimeProvider _timeProvider;
    private readonly IOptions<CameraOptions> _options;

    public CameraService(HomespoolDbContext dbContext,
                         CameraAccessService access,
                         CameraSourcePolicy sourcePolicy,
                         Go2RtcClient streamServer,
                         ICameraSnapshotFetcher fetcher,
                         CameraFrameCache frames,
                         CameraLiveAvailability liveView,
                         LocalCameraDevices devices,
                         CameraCredentialProtector credentials,
                         TimeProvider timeProvider,
                         IOptions<CameraOptions> options)
    {
        _dbContext = dbContext;
        _access = access;
        _sourcePolicy = sourcePolicy;
        _streamServer = streamServer;
        _fetcher = fetcher;
        _frames = frames;
        _liveView = liveView;
        _devices = devices;
        _credentials = credentials;
        _timeProvider = timeProvider;
        _options = options;
    }

    /// <summary>
    /// The attached devices nobody has claimed yet.
    /// </summary>
    /// <remarks>
    /// <b>Claimed devices are omitted rather than shown as unavailable.</b> A video device can only
    /// usefully be opened by one reader, so offering one twice would produce a camera that silently
    /// never works. Making the list mean "available" prevents that by construction, instead of by a
    /// rule somebody has to know - and deleting a camera hands its device back.
    /// </remarks>
    public async Task<IReadOnlyList<LocalCameraDevice>> AvailableDevicesAsync(CancellationToken cancellationToken)
    {
        List<string> claimed = await _dbContext.Cameras
                                               .Select(camera => camera.Source)
                                               .ToListAsync(cancellationToken)
                                               .ConfigureAwait(false);

        return _devices.List()
                       .Where(device => !claimed.Any(source => source.Contains(device.Name, StringComparison.Ordinal)))
                       .ToList();
    }

    /// <summary>
    /// The MJPEG capture sizes each of these devices offers, keyed by the by-id name the picker uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two vocabularies meet here.</b> Homespool names devices by their stable by-id entry, and
    /// the stream server enumerates <c>/dev/videoN</c> nodes; the symlink between them is readable
    /// without being followable, which is what lets this join the two without the application ever
    /// holding a capability to open a camera. See <see cref="LocalCameraDevices.NodeFor"/>.
    /// </para>
    /// <para>
    /// <b>A device with no entry is not an error.</b> The stream server enumerates once per process,
    /// so anything plugged in since it started is simply absent - the picker offers "whatever the
    /// camera provides" for those, and the rescan button restarts the sidecar for somebody who
    /// wants the list.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> DeviceResolutionsAsync(
        IReadOnlyList<LocalCameraDevice> devices,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(devices);

        Dictionary<string, IReadOnlyList<string>> byDevice = new(StringComparer.Ordinal);

        IReadOnlyDictionary<string, IReadOnlyList<string>>? byNode =
            await _streamServer.ListDeviceFormatsAsync(cancellationToken).ConfigureAwait(false);

        if (byNode is null)
        {
            return byDevice;
        }

        foreach (LocalCameraDevice device in devices)
        {
            if (_devices.NodeFor(device.Name) is { } node && byNode.TryGetValue(node, out IReadOnlyList<string>? sizes))
            {
                byDevice[device.Name] = sizes;
            }
        }

        return byDevice;
    }

    /// <summary>
    /// Restarts the stream server so it enumerates attached cameras again.
    /// </summary>
    /// <remarks>
    /// <b>The only way, and it is not free.</b> The stream server reads the device list once per
    /// process and caches it for its lifetime, so a camera plugged in afterwards cannot appear
    /// without a restart - which drops any live view and interrupts stills for a few seconds.
    /// Registered streams and the WebRTC candidate survive it, being written to the sidecar's own
    /// configuration. Offered as a button rather than done automatically, because the cost belongs
    /// to whoever chose to pay it.
    /// </remarks>
    public Task<bool> RescanDevicesAsync(CancellationToken cancellationToken)
    {
        return _streamServer.RestartAsync(cancellationToken);
    }

    /// <summary>
    /// Adds a camera, registers it, and finds out whether it actually produces a picture.
    /// </summary>
    /// <remarks>
    /// <paramref name="resolution"/> is the capture size asked of an attached camera, or null for
    /// whatever it offers - stored beside the source it was composed into, and null for every
    /// network camera. See <see cref="Camera.Resolution"/>.
    /// </remarks>
    public async Task<CameraSaveOutcome> CreateAsync(Caller caller,
                                                     int teamId,
                                                     string? name,
                                                     string source,
                                                     int? printerId,
                                                     string? resolution,
                                                     CancellationToken cancellationToken)
    {
        CameraSaveOutcome? refusal = await CheckPermittedAsync(caller, teamId, source, cancellationToken)
            .ConfigureAwait(false);

        if (refusal is not null)
        {
            return refusal;
        }

        // After the permission check rather than before it, so that a deployment's configuration is
        // only described to somebody who could otherwise have saved this camera.
        if (!_options.Value.IsAuthenticated)
        {
            return CameraSaveOutcome.Refused("Cameras_StreamServerNoCredential");
        }

        CameraSourceCheck check = await _sourcePolicy.CheckAsync(source, cancellationToken).ConfigureAwait(false);
        if (!check.IsAcceptable)
        {
            return CameraSaveOutcome.Refused(check.Error!);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();

        // Checked as one string above, because that is what the sidecar is handed; stored as two,
        // so the password does not sit in a column somebody can read.
        CameraCredential credential = _credentials.Split(source.Trim());

        Camera camera = new()
        {
            Uuid = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            Source = credential.Address,
            CredentialUser = credential.User,
            CredentialSecret = credential.Secret,
            Resolution = string.IsNullOrWhiteSpace(resolution) ? null : resolution.Trim(),
            TeamId = teamId,
            PrinterId = printerId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _dbContext.Cameras.Add(camera);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return await RegisterAndProveAsync(camera, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Changes a camera. Re-registers it, and forgets any frame from the old source.
    /// </summary>
    /// <remarks>
    /// <paramref name="resolution"/> decides the capture size of an attached camera, and the source
    /// is recomposed from it - see <see cref="LocalCameraDevices.WithResolution"/> for why the
    /// resolution wins rather than whatever <c>video_size</c> the submitted source carried.
    /// </remarks>
    public async Task<CameraSaveOutcome> UpdateAsync(Caller caller,
                                                     Guid uuid,
                                                     string? name,
                                                     string source,
                                                     int? printerId,
                                                     string? resolution,
                                                     CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        resolution = string.IsNullOrWhiteSpace(resolution) ? null : resolution.Trim();
        source = LocalCameraDevices.WithResolution(source.Trim(), resolution);

        Camera? camera = await _access
                               .FindAsync(uuid, caller, Capability.ManageCamera, cancellationToken)
                               .ConfigureAwait(false);

        if (camera is null)
        {
            return CameraSaveOutcome.Refused("Cameras_NotFoundOrNotYours");
        }

        // The edit form is shown the password as a placeholder, so an edit that did not touch it
        // posts the placeholder back. Put the stored one in before anything reads this source:
        // the policy check resolves it and the sidecar is handed it, and both need the real
        // credential. Restored from the revealed source rather than the column, since the column
        // holds the address alone once the password has been split out of it.
        source = CameraSourceDisplay.RestoreHiddenPassword(source, _credentials.Reveal(camera));

        CameraSaveOutcome? refusal = await CheckPermittedAsync(caller, camera.TeamId, source, cancellationToken)
            .ConfigureAwait(false);

        if (refusal is not null)
        {
            return refusal;
        }

        // After the permission check rather than before it, so that a deployment's configuration is
        // only described to somebody who could otherwise have saved this camera.
        if (!_options.Value.IsAuthenticated)
        {
            return CameraSaveOutcome.Refused("Cameras_StreamServerNoCredential");
        }

        CameraSourceCheck check = await _sourcePolicy.CheckAsync(source, cancellationToken).ConfigureAwait(false);
        if (!check.IsAcceptable)
        {
            return CameraSaveOutcome.Refused(check.Error!);
        }

        CameraCredential credential = _credentials.Split(source.Trim());

        camera.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        camera.Source = credential.Address;
        camera.CredentialUser = credential.User;
        camera.CredentialSecret = credential.Secret;

        // Null for a network camera whatever was submitted: nothing here reaches its settings, so a
        // stored size would be a claim this application cannot make good on.
        camera.Resolution = CameraSourcePolicy.IsLocalDevice(camera.Source) ? resolution : null;
        camera.PrinterId = printerId;
        camera.UpdatedAt = _timeProvider.GetUtcNow();

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Whatever is cached came from the old source. Keeping it would show the previous camera
        // under the new one's name for up to the maximum age - and the probed codec belongs to the
        // hardware at the old address, so it goes the same way.
        _frames.Forget(camera.Id);
        _liveView.Forget(camera.Uuid);

        return await RegisterAndProveAsync(camera, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a camera, its stream and its cached frame.
    /// </summary>
    public async Task<bool> DeleteAsync(Caller caller, Guid uuid, CancellationToken cancellationToken)
    {
        Camera? camera = await _access
                               .FindAsync(uuid, caller, Capability.ManageCamera, cancellationToken)
                               .ConfigureAwait(false);

        if (camera is null)
        {
            return false;
        }

        // An attached device is released by whoever holds it, so deleting is the release - but a
        // non-administrator must not be able to free one they could not have claimed.
        if (CameraSourcePolicy.IsLocalDevice(camera.Source)
            && !await _access.IsAdministratorAsync(caller.UserId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _dbContext.Cameras.Remove(camera);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _frames.Forget(camera.Id);
        _liveView.Forget(camera.Uuid);
        await _streamServer.DeleteStreamAsync(camera.Uuid, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Whether this account may put <paramref name="source"/> on a camera owned by this team, or
    /// the refusal to show them.
    /// </summary>
    private async Task<CameraSaveOutcome?> CheckPermittedAsync(Caller caller,
                                                               int teamId,
                                                               string source,
                                                               CancellationToken cancellationToken)
    {
        if (CameraSourcePolicy.IsLocalDevice(source))
        {
            bool isAdministrator = await _access.IsAdministratorAsync(caller.UserId, cancellationToken)
                                                .ConfigureAwait(false);

            return isAdministrator ?
                null :
                CameraSaveOutcome.Refused("Cameras_AttachedNeedsAdministrator");
        }

        TeamMember? membership = await _dbContext.TeamMembers
                                                 .FirstOrDefaultAsync(
                                                     member => member.TeamId == teamId && member.UserId == caller.UserId,
                                                     cancellationToken)
                                                 .ConfigureAwait(false);

        // Reading the row directly rather than through CameraAccessService is why the credential half
        // has to be spelled here too: a scope must not slip past a gate because it went round it.
        if (!caller.Allows(Capability.ManageCamera))
        {
            throw CredentialScopeDeniedException.For(Capability.ManageCamera);
        }

        bool permitted = membership is not null
                         && CapabilitySet.Parse(membership.Capabilities).Allows(Capability.ManageCamera);

        return permitted ?
            null :
            CameraSaveOutcome.Refused("Cameras_NotYourTeam");
    }

    /// <summary>
    /// Registers the camera with the stream server and asks it for one frame, so the outcome
    /// describes what happened rather than what was intended.
    /// </summary>
    /// <remarks>
    /// A failure here does not undo the save. A camera being switched off while it is set up is
    /// ordinary, and refusing to remember its address until it answers would be worse than saying
    /// it did not.
    /// </remarks>
    private async Task<CameraSaveOutcome> RegisterAndProveAsync(Camera camera, CancellationToken cancellationToken)
    {
        bool registered = await _streamServer
                                .PutStreamAsync(camera.Uuid, _credentials.Reveal(camera), cancellationToken)
                                .ConfigureAwait(false);

        if (!registered)
        {
            return CameraSaveOutcome.Silent(camera, "Cameras_StreamServerRefused");
        }

        // Null only when the sidecar has no credential, which PutStreamAsync above has already
        // refused for - so this is unreachable in practice and written as a fall-through rather than
        // a suppression, because "unreachable" is a claim about today's call order.
        Uri? frameUrl = _streamServer.FrameUrl(camera.Uuid);

        CameraFrame? frame = frameUrl is null ?
            null :
            await _fetcher.FetchAsync(frameUrl, cancellationToken).ConfigureAwait(false);

        if (frame is not null)
        {
            // The camera just proved it is on, which is the one moment a codec probe is guaranteed
            // to get an answer - so the live-view button is warm before any page asks, instead of
            // the first viewer paying for the probe. The answer is thrown away here because the
            // availability remembers it; a failure costs nothing, since the next /live ask probes
            // again anyway.
            _ = await _liveView.HowToWatchAsync(camera.Uuid, cancellationToken).ConfigureAwait(false);

            return CameraSaveOutcome.Working(camera);
        }

        // The two kinds fail for different reasons, and the wrong hint sends somebody looking in
        // the wrong place: a network camera is usually off or unreachable, while an attached one is
        // usually a device the stream server cannot open.
        return CameraSaveOutcome.Silent(
            camera,
            CameraSourcePolicy.IsLocalDevice(camera.Source) ?
                "Cameras_NoPictureLocal" :
                "Cameras_NoPictureNetwork");
    }
}
