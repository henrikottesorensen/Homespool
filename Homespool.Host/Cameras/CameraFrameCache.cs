using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Cameras;

/// <summary>
/// Holds the most recent frame from each camera, and refreshes it while somebody is looking.
/// </summary>
/// <remarks>
/// <para>
/// <b>Demand is the signal.</b> There is no background service and no polling loop: asking for a
/// frame is what schedules the next fetch, so a page left open keeps its camera current and a
/// camera nobody is watching costs nothing at all. Closing the page stops the work with no gating
/// logic to write, and no way for that logic to be wrong.
/// </para>
/// <para>
/// <b>A request never waits for a fetch.</b> The current frame is returned immediately and the
/// refresh happens behind it, so a slow camera cannot make a page slow — measured, an RTSP camera
/// takes 2-3.5 s to produce a frame. The cost is that the very first request after a quiet period
/// has nothing to hand back; the caller shows that it is capturing, and the next poll a couple of
/// seconds later has one.
/// </para>
/// <para>
/// <b>Nothing stale is ever served.</b> Past <see cref="CameraOptions.MaxAgeSeconds"/> a frame is
/// dropped rather than handed over with a caption, because the failure this guards against is a
/// person acting on an old photograph that looks exactly like a current one.
/// </para>
/// <para>
/// Frames live here and nowhere else: no table, no file, no retention. Timelapse would need frames
/// captured when nobody is watching, which this deliberately does not do.
/// </para>
/// </remarks>
public sealed class CameraFrameCache
{
    private readonly ConcurrentDictionary<int, Entry> _entries = new();
    private readonly ICameraSnapshotFetcher _fetcher;
    private readonly IOptions<CameraOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CameraFrameCache> _logger;

    public CameraFrameCache(
        ICameraSnapshotFetcher fetcher,
        IOptions<CameraOptions> options,
        TimeProvider timeProvider,
        ILogger<CameraFrameCache> logger)
    {
        _fetcher = fetcher;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// The camera's current frame, or <see langword="null"/> if there is none fresh enough to show.
    /// </summary>
    /// <remarks>
    /// A frame past its maximum age is discarded here rather than returned, so a caller cannot
    /// accidentally render one by ignoring its age.
    /// </remarks>
    public CameraFrame? Current(int cameraId)
    {
        if (!_entries.TryGetValue(cameraId, out Entry? entry))
        {
            return null;
        }

        CameraFrame? frame = entry.Frame;
        if (frame is null)
        {
            return null;
        }

        TimeSpan maxAge = TimeSpan.FromSeconds(_options.Value.MaxAgeSeconds);
        if (frame.AgeAt(_timeProvider.GetUtcNow()) > maxAge)
        {
            entry.Frame = null;
            return null;
        }

        return frame;
    }

    /// <summary>
    /// Asks for the camera to be refreshed, unless it was asked too recently or is already being
    /// fetched. Returns immediately either way.
    /// </summary>
    /// <remarks>
    /// The in-flight guard is what stops a slow camera queueing behind itself: the next attempt is
    /// scheduled from the completion of the last, not on a fixed tick, so a camera taking 3 s is
    /// asked every 3 s rather than accumulating three outstanding fetches.
    /// </remarks>
    public void RequestRefresh(int cameraId, Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);

        Entry entry = _entries.GetOrAdd(cameraId, static _ => new Entry());

        DateTimeOffset now = _timeProvider.GetUtcNow();
        TimeSpan floor = TimeSpan.FromSeconds(_options.Value.RefreshFloorSeconds);

        lock (entry.Gate)
        {
            if (entry.InFlight)
            {
                return;
            }

            if (entry.LastAttemptAt is DateTimeOffset last && now - last < floor)
            {
                return;
            }

            entry.InFlight = true;
            entry.LastAttemptAt = now;
        }

        _ = RefreshAsync(entry, cameraId, address);
    }

    /// <summary>
    /// Forgets everything known about a camera. Called when one is deleted or its address changes,
    /// so a frame from the old address can never be shown against the new one.
    /// </summary>
    public void Forget(int cameraId)
    {
        _entries.TryRemove(cameraId, out _);
    }

    private async Task RefreshAsync(Entry entry, int cameraId, Uri address)
    {
        try
        {
            // Deliberately not the caller's cancellation token: the refresh outlives the request
            // that triggered it, which is the whole point of not making that request wait. The
            // fetcher's own timeout is what bounds it.
            CameraFrame? frame = await _fetcher.FetchAsync(address, CancellationToken.None)
                                               .ConfigureAwait(false);

            if (frame is not null)
            {
                entry.Frame = frame;
            }
        }
        catch (Exception exception)
        {
            // The fetcher answers null for every expected failure, so anything arriving here is a
            // defect rather than an unreachable camera. Swallowed all the same: this runs detached,
            // and an unobserved exception on a background task would take the process down.
            _logger.LogError(exception, "Unexpected failure refreshing camera {CameraId}.", cameraId);
        }
        finally
        {
            lock (entry.Gate)
            {
                entry.InFlight = false;

                // Stamped again on completion so the floor measures the gap between finishing and
                // starting again, rather than between two starts - which is what keeps a camera
                // slower than the floor from being asked continuously.
                entry.LastAttemptAt = _timeProvider.GetUtcNow();
            }
        }
    }

    private sealed class Entry
    {
        public object Gate { get; } = new();

        private CameraFrame? _frame;

        /// <summary>
        /// The last frame fetched. Read outside the lock by <see cref="Current"/>, so the accesses
        /// are volatile: a torn or cached read here would show a stale frame after a refresh.
        /// </summary>
        public CameraFrame? Frame
        {
            get => Volatile.Read(ref _frame);
            set => Volatile.Write(ref _frame, value);
        }

        public bool InFlight { get; set; }

        public DateTimeOffset? LastAttemptAt { get; set; }
    }
}
