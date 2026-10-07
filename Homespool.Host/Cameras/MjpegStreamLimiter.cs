using System;
using System.Collections.Generic;
using System.Threading;

using Microsoft.Extensions.Options;

namespace Homespool.Host.Cameras;

/// <summary>
/// Counts the live MJPEG streams each account has open, and refuses one past
/// <see cref="CameraOptions.MaxMjpegStreamsPerUser"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A count of what is open, not a rate of requests.</b> What a stream costs is held for as long as
/// it stays open, so a window of requests per minute would let a slow trickle pile up without end,
/// while refusing a viewer who reloads a few times and holds one.
/// </para>
/// <para>
/// <b>The limit is read on every request</b>, so lowering it refuses new streams at once and leaves
/// the ones already open alone. They end when their viewer leaves, which is soon enough for a limit
/// on something a person is watching.
/// </para>
/// <para>
/// <b>In memory, and that is exact rather than approximate.</b> A stream lives no longer than the
/// process relaying it, so a count that starts again at zero with the process is still right.
/// </para>
/// <para>
/// <b>It is also where a stream is stopped from outside.</b> A page names its live view when it opens
/// the stream, and <see cref="Stop"/> ends that view on request - because Safari, measured on the
/// appliance, keeps a multipart picture's connection open after the page takes its source away, for
/// as long as the tab stays open, and the only end it cannot ignore is the server's. The one place that
/// already holds every open stream is the one that can find it.
/// </para>
/// </remarks>
public sealed class MjpegStreamLimiter
{
    private readonly IOptionsMonitor<CameraOptions> _options;
    private readonly Dictionary<long, int> _open = [];
    private readonly Dictionary<(long userId, Guid camera, Guid view), MjpegStreamLease> _views = [];
    private readonly Lock _lock = new();

    /// <summary>Creates the limiter.</summary>
    /// <param name="options">Where the limit is read, on every request.</param>
    public MjpegStreamLimiter(IOptionsMonitor<CameraOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
    }

    /// <summary>
    /// Takes one of the account's streams, or answers <see langword="null"/> when it already has as
    /// many open as it may.
    /// </summary>
    /// <param name="userId">The account opening the stream.</param>
    /// <param name="camera">The camera it is of.</param>
    /// <param name="view">
    /// The page's name for this live view, by which <see cref="Stop"/> finds it - or
    /// <see langword="null"/> for a caller with no way to stop it but leaving, which is any script. A
    /// name already in use by the same account for the same camera is not taken over: the stream is
    /// counted, and only the first holder of the name can be stopped by it.
    /// </param>
    /// <returns>A lease that gives the stream back when disposed, or <see langword="null"/>.</returns>
    public MjpegStreamLease? TryAcquire(long userId, Guid camera, Guid? view)
    {
        int limit = _options.CurrentValue.MaxMjpegStreamsPerUser;

        lock (_lock)
        {
            _open.TryGetValue(userId, out int count);

            if (count >= limit)
            {
                return null;
            }

            _open[userId] = count + 1;

            if (view is Guid named && !_views.ContainsKey((userId, camera, named)))
            {
                MjpegStreamLease stoppable = new(this, userId, (userId, camera, named));
                _views.Add((userId, camera, named), stoppable);

                return stoppable;
            }

            return new MjpegStreamLease(this, userId, null);
        }
    }

    /// <summary>
    /// Stops the account's named live view of <paramref name="camera"/>: the stream it is relaying
    /// sees <see cref="MjpegStreamLease.Stopped"/> and ends, breaking the viewer's connection.
    /// </summary>
    /// <returns>
    /// Whether there was such a view to stop. Another account's view is never found, so a stranger's
    /// request cannot tell it from one that does not exist.
    /// </returns>
    public bool Stop(long userId, Guid camera, Guid view)
    {
        MjpegStreamLease? lease;

        lock (_lock)
        {
            _views.TryGetValue((userId, camera, view), out lease);
        }

        // Outside the lock: cancelling runs the stream's own continuations, which give the lease back
        // through this same lock.
        lease?.Cancel();

        return lease is not null;
    }

    /// <summary>Gives a stream back, and forgets its name.</summary>
    internal void Release(MjpegStreamLease lease)
    {
        lock (_lock)
        {
            if (lease.Key is { } key)
            {
                _views.Remove(key);
            }

            long userId = lease.UserId;

            if (!_open.TryGetValue(userId, out int count))
            {
                return;
            }

            // Removed at zero rather than left behind, so an account that once watched does not keep
            // an entry for the life of the process.
            if (count <= 1)
            {
                _open.Remove(userId);
            }
            else
            {
                _open[userId] = count - 1;
            }
        }
    }
}
