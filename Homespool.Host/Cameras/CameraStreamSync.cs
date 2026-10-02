using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Model.Entities;

namespace Homespool.Host.Cameras;

/// <summary>
/// The one way a camera's stream is written to the sidecar: it reads the camera's row and makes the
/// sidecar hold what the row says.
/// </summary>
/// <remarks>
/// <para>
/// <b>The database is the truth, and the sidecar's file is a copy of it.</b> Nothing hands this a
/// source. Every caller - a save, a removal, the sweep, the startup reconciler - names a camera, and
/// the row is read here, inside that camera's gate, at the moment its stream is written. A caller
/// that read the row earlier and waited, as the reconciler waits for a restarting sidecar, cannot
/// then write what it read: an edit or a removal made in the wait is what arrives.
/// </para>
/// <para>
/// <b>One camera at a time, per camera.</b> Two writes for the same camera are serialised, so the
/// last one to finish is the one that read the row last. Without that, two saves of one camera can
/// commit in one order and reach the sidecar in the other, and the sidecar keeps the older source
/// with nothing left to correct it. Different cameras do not wait for each other.
/// </para>
/// <para>
/// <b>A source the sidecar already holds is left alone.</b> A <c>PUT</c> replaces the stream even
/// when nothing changed, and a viewer already watching keeps the stream it had: the camera then has
/// two readers, which an attached camera cannot serve. Measured on go2rtc 1.9.14, where the second
/// viewer opened a second connection to the camera while the first was still being fed from the
/// replaced stream.
/// </para>
/// <para>
/// <b>The rules about what may be handed over are applied here, on every write.</b> A row written
/// before a rule existed, or by anything that went round <see cref="CameraService"/>, is checked
/// before it reaches the sidecar - and a stream the sidecar already holds for a source that now fails
/// is removed rather than kept. A name that does not resolve is accepted, as the reconciler always
/// accepted it; a save has already refused one before its row was written.
/// </para>
/// </remarks>
public sealed class CameraStreamSync
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Go2RtcClient _streamServer;
    private readonly CameraCredentialProtector _credentials;
    private readonly LocalCameraDevices _devices;
    private readonly CameraSourcePolicy _policy;
    private readonly ILogger<CameraStreamSync> _logger;

    private readonly Lock _gatesLock = new();
    private readonly Dictionary<Guid, Gate> _gates = [];

    public CameraStreamSync(IServiceScopeFactory scopeFactory,
                            Go2RtcClient streamServer,
                            CameraCredentialProtector credentials,
                            LocalCameraDevices devices,
                            CameraSourcePolicy policy,
                            ILogger<CameraStreamSync> logger)
    {
        _scopeFactory = scopeFactory;
        _streamServer = streamServer;
        _credentials = credentials;
        _devices = devices;
        _policy = policy;
        _logger = logger;
    }

    /// <summary>
    /// Makes the sidecar's stream for <paramref name="uuid"/> match that camera's row as it is now:
    /// registered if the row's source passes and the sidecar does not hold it, left alone if it does,
    /// and removed if there is no row or its source fails.
    /// </summary>
    public async Task<StreamSync> SyncAsync(Guid uuid, CancellationToken cancellationToken)
    {
        Gate gate = Enter(uuid);

        try
        {
            await gate.Turn.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return await SyncInTurnAsync(uuid, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Turn.Release();
            }
        }
        finally
        {
            Leave(uuid, gate);
        }
    }

    private async Task<StreamSync> SyncInTurnAsync(Guid uuid, CancellationToken cancellationToken)
    {
        Camera? camera;

        using (IServiceScope scope = _scopeFactory.CreateScope())
        {
            camera = await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                                .Cameras
                                .AsNoTracking()
                                .SingleOrDefaultAsync(row => row.Uuid == uuid, cancellationToken)
                                .ConfigureAwait(false);
        }

        if (camera is null)
        {
            return await _streamServer.DeleteStreamAsync(uuid, cancellationToken).ConfigureAwait(false) ?
                new StreamSync(StreamSyncOutcome.Removed) :
                new StreamSync(StreamSyncOutcome.Unavailable);
        }

        string source = _credentials.Reveal(camera);

        CameraSourceCheck check = await CheckAsync(camera, source, cancellationToken).ConfigureAwait(false);

        if (!check.IsAcceptable)
        {
            // The source itself is deliberately not logged: it is the thing under suspicion, and a
            // log line is a place it would then be read from.
            _logger.LogWarning(
                "Camera {Uuid}'s source is not one this server hands to the stream server ({Reason}), so it is " +
                "not registered there. Open it on the cameras page and save it to see why.",
                uuid,
                check.Error?.Key);

            _ = await _streamServer.DeleteStreamAsync(uuid, cancellationToken).ConfigureAwait(false);

            return new StreamSync(StreamSyncOutcome.Withheld, check.Error);
        }

        if (await HoldsAsync(uuid, source, cancellationToken).ConfigureAwait(false))
        {
            return new StreamSync(StreamSyncOutcome.Unchanged);
        }

        return (await _streamServer.PutStreamAsync(uuid, source, cancellationToken).ConfigureAwait(false)) switch
        {
            StreamRegistration.Registered => new StreamSync(StreamSyncOutcome.Registered),
            StreamRegistration.SourceRefused => new StreamSync(StreamSyncOutcome.SourceRefused),
            StreamRegistration.ConfigurationNotSaved => new StreamSync(StreamSyncOutcome.ConfigurationNotSaved),
            _ => new StreamSync(StreamSyncOutcome.Unavailable),
        };
    }

    /// <summary>
    /// The rule for an attached camera's source, or for a network one.
    /// </summary>
    private async Task<CameraSourceCheck> CheckAsync(Camera camera, string source, CancellationToken cancellationToken)
    {
        if (CameraSourcePolicy.IsLocalDevice(source))
        {
            return LocalCameraDevices.CheckComposed(source,
                                                    camera.Resolution,
                                                    _devices.List().Select(device => device.Name));
        }

        // What a name points at is decided by whoever controls the name, and can have changed since
        // the save that checked it - so it is asked again here, where the source is handed over. The
        // same check also refuses what a source holds, not only where it points, so a camera saved
        // before one of those rules existed stops here too.
        return await _policy.CheckAsync(source, acceptUnresolvable: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the sidecar holds a stream for <paramref name="uuid"/> with exactly
    /// <paramref name="source"/> as its one source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asked of the listing and of the file, and both must agree.</b> The file says which source a
    /// stream was given, which the listing cannot while it is being watched; the listing says the
    /// stream is running, which the file cannot - a removal whose file write failed takes the stream
    /// out of the sidecar and leaves it in the file.
    /// </para>
    /// <para>
    /// Either one unreadable answers no, and the source is registered: a replacement nobody needed
    /// costs less than a camera left unregistered.
    /// </para>
    /// </remarks>
    private async Task<bool> HoldsAsync(Guid uuid, string source, CancellationToken cancellationToken)
    {
        string name = uuid.ToString("D", CultureInfo.InvariantCulture);

        IReadOnlySet<string>? running = await _streamServer.ListStreamNamesAsync(cancellationToken).ConfigureAwait(false);

        if (running is null || !running.Contains(name))
        {
            return false;
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>>? saved =
            await _streamServer.ReadStreamSourcesAsync(cancellationToken).ConfigureAwait(false);

        return saved is not null &&
               saved.TryGetValue(name, out IReadOnlyList<string>? sources) &&
               sources.SequenceEqual([source], StringComparer.Ordinal);
    }

    private Gate Enter(Guid uuid)
    {
        lock (_gatesLock)
        {
            if (!_gates.TryGetValue(uuid, out Gate? gate))
            {
                gate = new Gate();
                _gates[uuid] = gate;
            }

            gate.Holders++;

            return gate;
        }
    }

    /// <summary>
    /// Forgets the gate once nobody holds it, so a camera removed long ago does not keep one.
    /// </summary>
    private void Leave(Guid uuid, Gate gate)
    {
        lock (_gatesLock)
        {
            if (--gate.Holders == 0)
            {
                _gates.Remove(uuid);
                gate.Turn.Dispose();
            }
        }
    }

    /// <summary>
    /// One camera's turn, and how many callers are waiting for it or holding it.
    /// </summary>
    private sealed class Gate
    {
        public SemaphoreSlim Turn { get; } = new(1, 1);

        public int Holders { get; set; }
    }
}
