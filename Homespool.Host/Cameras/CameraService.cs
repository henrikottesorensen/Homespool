using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Authorisation;
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
    private readonly HSDbContext _dbContext;
    private readonly CameraAccessService _access;
    private readonly CameraSourcePolicy _sourcePolicy;
    private readonly Go2RtcClient _streamServer;
    private readonly ICameraSnapshotFetcher _fetcher;
    private readonly CameraFrameCache _frames;
    private readonly LocalCameraDevices _devices;
    private readonly TimeProvider _timeProvider;

    public CameraService(
        HSDbContext dbContext,
        CameraAccessService access,
        CameraSourcePolicy sourcePolicy,
        Go2RtcClient streamServer,
        ICameraSnapshotFetcher fetcher,
        CameraFrameCache frames,
        LocalCameraDevices devices,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _access = access;
        _sourcePolicy = sourcePolicy;
        _streamServer = streamServer;
        _fetcher = fetcher;
        _frames = frames;
        _devices = devices;
        _timeProvider = timeProvider;
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
                       .Where(device => !claimed.Any(
                           source => source.Contains(device.Name, StringComparison.Ordinal)))
                       .ToList();
    }

    /// <summary>
    /// Adds a camera, registers it, and finds out whether it actually produces a picture.
    /// </summary>
    public async Task<CameraSaveOutcome> CreateAsync(
        long userId,
        int teamId,
        string? name,
        string source,
        int? printerId,
        CancellationToken cancellationToken)
    {
        CameraSaveOutcome? refusal = await CheckPermittedAsync(userId, teamId, source, cancellationToken)
            .ConfigureAwait(false);

        if (refusal is not null)
        {
            return refusal;
        }

        CameraSourceCheck check = await _sourcePolicy.CheckAsync(source, cancellationToken).ConfigureAwait(false);
        if (!check.IsAcceptable)
        {
            return CameraSaveOutcome.Refused(check.Error!);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();

        Camera camera = new()
        {
            Uuid = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            Source = source.Trim(),
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
    public async Task<CameraSaveOutcome> UpdateAsync(
        long userId,
        Guid uuid,
        string? name,
        string source,
        int? printerId,
        CancellationToken cancellationToken)
    {
        Camera? camera = await _access
            .FindAsync(uuid, userId, CameraOperation.ManageCamera, cancellationToken)
            .ConfigureAwait(false);

        if (camera is null)
        {
            return CameraSaveOutcome.Refused("That camera does not exist, or is not yours to change.");
        }

        CameraSaveOutcome? refusal = await CheckPermittedAsync(userId, camera.TeamId, source, cancellationToken)
            .ConfigureAwait(false);

        if (refusal is not null)
        {
            return refusal;
        }

        CameraSourceCheck check = await _sourcePolicy.CheckAsync(source, cancellationToken).ConfigureAwait(false);
        if (!check.IsAcceptable)
        {
            return CameraSaveOutcome.Refused(check.Error!);
        }

        camera.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        camera.Source = source.Trim();
        camera.PrinterId = printerId;
        camera.UpdatedAt = _timeProvider.GetUtcNow();

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Whatever is cached came from the old source. Keeping it would show the previous camera
        // under the new one's name for up to the maximum age.
        _frames.Forget(camera.Id);

        return await RegisterAndProveAsync(camera, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a camera, its stream and its cached frame.
    /// </summary>
    public async Task<bool> DeleteAsync(long userId, Guid uuid, CancellationToken cancellationToken)
    {
        Camera? camera = await _access
            .FindAsync(uuid, userId, CameraOperation.ManageCamera, cancellationToken)
            .ConfigureAwait(false);

        if (camera is null)
        {
            return false;
        }

        // An attached device is released by whoever holds it, so deleting is the release - but a
        // non-administrator must not be able to free one they could not have claimed.
        if (CameraSourcePolicy.IsLocalDevice(camera.Source)
            && !await _access.IsAdministratorAsync(userId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _dbContext.Cameras.Remove(camera);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _frames.Forget(camera.Id);
        await _streamServer.DeleteStreamAsync(camera.Uuid, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Whether this account may put <paramref name="source"/> on a camera owned by this team, or
    /// the refusal to show them.
    /// </summary>
    private async Task<CameraSaveOutcome?> CheckPermittedAsync(
        long userId, int teamId, string source, CancellationToken cancellationToken)
    {
        if (CameraSourcePolicy.IsLocalDevice(source))
        {
            bool isAdministrator = await _access.IsAdministratorAsync(userId, cancellationToken)
                                                .ConfigureAwait(false);

            return isAdministrator
                ? null
                : CameraSaveOutcome.Refused(
                    "A camera plugged into this server can only be set up by an administrator. "
                    + "A camera reachable over the network does not need one.");
        }

        TeamMember? membership = await _dbContext.TeamMembers
            .FirstOrDefaultAsync(
                member => member.TeamId == teamId && member.UserId == userId, cancellationToken)
            .ConfigureAwait(false);

        return membership is not null && membership.CanManage
            ? null
            : CameraSaveOutcome.Refused("You cannot add a camera to that team.");
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
            .PutStreamAsync(camera.Uuid, camera.Source, cancellationToken)
            .ConfigureAwait(false);

        if (!registered)
        {
            return CameraSaveOutcome.Silent(
                camera,
                "Saved, but the stream server would not accept this source. Check the address, or "
                + "that the go2rtc service is running.");
        }

        CameraFrame? frame = await _fetcher
            .FetchAsync(_streamServer.FrameUrl(camera.Uuid), cancellationToken)
            .ConfigureAwait(false);

        if (frame is not null)
        {
            return CameraSaveOutcome.Working(camera);
        }

        // The two kinds fail for different reasons, and the wrong hint sends somebody looking in
        // the wrong place: a network camera is usually off or unreachable, while an attached one is
        // usually a device the stream server cannot open.
        return CameraSaveOutcome.Silent(
            camera,
            CameraSourcePolicy.IsLocalDevice(camera.Source)
                ? "Saved, but no picture came back. The stream server may not have access to this "
                  + "camera: check that the go2rtc service in compose.yaml can reach video devices, "
                  + "and that nothing else on this machine is already using it."
                : "Saved, but no picture came back yet. If the camera is switched on and reachable, "
                  + "try the page again in a moment - some cameras take a few seconds to answer.");
    }
}
