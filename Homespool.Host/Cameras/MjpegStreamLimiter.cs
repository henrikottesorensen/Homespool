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
/// </remarks>
public sealed class MjpegStreamLimiter
{
    private readonly IOptionsMonitor<CameraOptions> _options;
    private readonly Dictionary<long, int> _open = [];
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
    /// <returns>A lease that gives the stream back when disposed, or <see langword="null"/>.</returns>
    public IDisposable? TryAcquire(long userId)
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
        }

        return new Lease(this, userId);
    }

    private void Release(long userId)
    {
        lock (_lock)
        {
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

    /// <summary>One open stream. Disposing it more than once gives back one stream, not several.</summary>
    private sealed class Lease : IDisposable
    {
        private readonly long _userId;
        private MjpegStreamLimiter? _owner;

        public Lease(MjpegStreamLimiter owner, long userId)
        {
            _owner = owner;
            _userId = userId;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(_userId);
        }
    }
}
