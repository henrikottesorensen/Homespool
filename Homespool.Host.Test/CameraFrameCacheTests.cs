using System;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Homespool.Host.Cameras;

namespace Homespool.Host.Test;

/// <summary>
/// The cache's two jobs: never serve an image old enough to mislead, and never ask a camera more
/// often than it can answer.
/// </summary>
/// <remarks>
/// The fetcher is substituted and answers from a completed task, so the refresh started by
/// <see cref="CameraFrameCache.RequestRefresh"/> runs to completion synchronously. That keeps these
/// tests deterministic without a sleep anywhere.
/// </remarks>
public class CameraFrameCacheTests
{
    private const int CameraId = 7;

    private static readonly DateTimeOffset Start = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private static readonly Uri Address = new("http://go2rtc:1984/api/frame.jpeg?src=abc");

    [Fact]
    public void ACameraNeverFetchedHasNothingToShow()
    {
        (CameraFrameCache cache, _, _) = Build();

        cache.Current(CameraId).Should().BeNull();
    }

    [Fact]
    public void AFetchedFrameIsServed()
    {
        (CameraFrameCache cache, _, FakeTimeProvider time) = Build();

        cache.RequestRefresh(CameraId, Address);

        CameraFrame? frame = cache.Current(CameraId);
        frame.Should().NotBeNull();
        frame!.CapturedAt.Should().Be(time.GetUtcNow());
    }

    /// <summary>
    /// The behaviour this whole design exists for. A camera nobody has looked at for a day still
    /// holds yesterday's frame in memory; serving it would show a day-old photograph of a clear
    /// print bed, which looks exactly like a current one.
    /// </summary>
    [Fact]
    public void AFrameOlderThanTheMaximumAgeIsDiscardedRatherThanServed()
    {
        (CameraFrameCache cache, _, FakeTimeProvider time) = Build(maxAgeSeconds: 60);

        cache.RequestRefresh(CameraId, Address);
        cache.Current(CameraId).Should().NotBeNull("the frame is fresh to begin with");

        time.Advance(TimeSpan.FromSeconds(61));

        cache.Current(CameraId).Should().BeNull("a stale frame is dropped, not captioned");
    }

    [Fact]
    public void ASecondRequestInsideTheFloorDoesNotFetchAgain()
    {
        (CameraFrameCache cache, ICameraSnapshotFetcher fetcher, FakeTimeProvider time) =
            Build(refreshFloorSeconds: 2);

        cache.RequestRefresh(CameraId, Address);
        time.Advance(TimeSpan.FromSeconds(1));
        cache.RequestRefresh(CameraId, Address);

        fetcher.Received(1).FetchAsync(Address, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void OnceTheFloorHasPassedTheCameraIsAskedAgain()
    {
        (CameraFrameCache cache, ICameraSnapshotFetcher fetcher, FakeTimeProvider time) =
            Build(refreshFloorSeconds: 2);

        cache.RequestRefresh(CameraId, Address);
        time.Advance(TimeSpan.FromSeconds(3));
        cache.RequestRefresh(CameraId, Address);

        fetcher.Received(2).FetchAsync(Address, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A camera whose address changed must not go on showing what the old one served.
    /// </summary>
    [Fact]
    public void ForgettingACameraDropsItsFrame()
    {
        (CameraFrameCache cache, _, _) = Build();

        cache.RequestRefresh(CameraId, Address);
        cache.Current(CameraId).Should().NotBeNull();

        cache.Forget(CameraId);

        cache.Current(CameraId).Should().BeNull();
    }

    /// <summary>
    /// A camera that cannot be read leaves the cache empty rather than poisoning it, and does not
    /// throw out of the detached refresh.
    /// </summary>
    [Fact]
    public void AnUnreadableCameraLeavesNothingBehind()
    {
        (CameraFrameCache cache, ICameraSnapshotFetcher fetcher, _) = Build();
        fetcher.FetchAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<CameraFrame?>(null));

        cache.RequestRefresh(CameraId, Address);

        cache.Current(CameraId).Should().BeNull();
    }

    private static (CameraFrameCache cache, ICameraSnapshotFetcher fetcher, FakeTimeProvider time) Build(
        int refreshFloorSeconds = 2,
        int maxAgeSeconds = 60)
    {
        FakeTimeProvider time = new(Start);
        ICameraSnapshotFetcher fetcher = Substitute.For<ICameraSnapshotFetcher>();

        fetcher.FetchAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromResult<CameraFrame?>(
                            new CameraFrame([1, 2, 3], "image/jpeg", time.GetUtcNow())));

        CameraOptions options = new()
        {
            RefreshFloorSeconds = refreshFloorSeconds,
            MaxAgeSeconds = maxAgeSeconds,
        };

        CameraFrameCache cache = new(
            fetcher, TestOptions.Monitor(options), time, NullLogger<CameraFrameCache>.Instance);

        return (cache, fetcher, time);
    }
}
