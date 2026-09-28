using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Homespool.Host.Cameras;

namespace Homespool.Host.Test;

/// <summary>
/// The per-account ceiling on live MJPEG streams: what it allows, what it refuses, and that a stream
/// ending gives its place back - and stopping a named view from outside, which only its own account
/// can do.
/// </summary>
public class MjpegStreamLimiterTests
{
    private const long Alice = 1;
    private const long Bob = 2;

    private static readonly Guid Camera = Guid.NewGuid();

    [Fact]
    public void TheDefaultIsFive()
    {
        new CameraOptions().MaxMjpegStreamsPerUser.Should().Be(5);
    }

    [Fact]
    public void AnAccountMayOpenUpToTheLimitAndNoMore()
    {
        MjpegStreamLimiter limiter = new(TestOptions.Monitor(new CameraOptions()));

        IDisposable?[] open = [.. Enumerable.Range(0, 5).Select(_ => limiter.TryAcquire(Alice, Camera, null))];

        try
        {
            open.Should().NotContainNulls("five is the default");

            limiter.TryAcquire(Alice, Camera, null).Should().BeNull("the sixth is one past it");
        }
        finally
        {
            foreach (IDisposable? lease in open)
            {
                lease?.Dispose();
            }
        }
    }

    [Fact]
    public void AStreamEndingGivesItsPlaceBack()
    {
        MjpegStreamLimiter limiter = new(TestOptions.Monitor(new CameraOptions { MaxMjpegStreamsPerUser = 1 }));

        IDisposable? first = limiter.TryAcquire(Alice, Camera, null);
        first.Should().NotBeNull();
        limiter.TryAcquire(Alice, Camera, null).Should().BeNull();

        first!.Dispose();

        using IDisposable? again = limiter.TryAcquire(Alice, Camera, null);
        again.Should().NotBeNull("the viewer left, so the account may open another");
    }

    /// <summary>
    /// The reason the ceiling is per account: one account at its limit must not close live view for
    /// anybody else.
    /// </summary>
    [Fact]
    public void OneAccountAtItsLimitDoesNotRefuseAnother()
    {
        MjpegStreamLimiter limiter = new(TestOptions.Monitor(new CameraOptions { MaxMjpegStreamsPerUser = 1 }));

        using IDisposable? alice = limiter.TryAcquire(Alice, Camera, null);
        using IDisposable? bob = limiter.TryAcquire(Bob, Camera, null);

        alice.Should().NotBeNull();
        bob.Should().NotBeNull();
    }

    [Fact]
    public void DisposingALeaseTwiceGivesBackOneStreamNotTwo()
    {
        MjpegStreamLimiter limiter = new(TestOptions.Monitor(new CameraOptions { MaxMjpegStreamsPerUser = 2 }));

        IDisposable? first = limiter.TryAcquire(Alice, Camera, null);
        using IDisposable? second = limiter.TryAcquire(Alice, Camera, null);

        first!.Dispose();
        first.Dispose();

        using IDisposable? third = limiter.TryAcquire(Alice, Camera, null);
        third.Should().NotBeNull("the first lease's place came back once");
        limiter.TryAcquire(Alice, Camera, null).Should().BeNull("and only once, so two are open again");
    }

    /// <summary>
    /// Graded live on the settings page, so a lowered limit has to apply to the next request rather
    /// than the next start. Streams already open are left to end on their own.
    /// </summary>
    [Fact]
    public void ALoweredLimitRefusesTheNextStreamWithoutARestart()
    {
        ChangeableMonitor<CameraOptions> options = TestOptions.Monitor(new CameraOptions { MaxMjpegStreamsPerUser = 3 });
        MjpegStreamLimiter limiter = new(options);

        using IDisposable? first = limiter.TryAcquire(Alice, Camera, null);
        using IDisposable? second = limiter.TryAcquire(Alice, Camera, null);

        options.Set(new CameraOptions { MaxMjpegStreamsPerUser = 2 });

        limiter.TryAcquire(Alice, Camera, null).Should().BeNull("two are open and two is now the limit");
    }

    [Fact]
    public async Task ConcurrentOpensNeverExceedTheLimit()
    {
        MjpegStreamLimiter limiter = new(TestOptions.Monitor(new CameraOptions()));
        ConcurrentBag<IDisposable> granted = [];

        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() =>
        {
            if (limiter.TryAcquire(Alice, Camera, null) is { } lease)
            {
                granted.Add(lease);
            }
        })));

        try
        {
            granted.Should().HaveCount(5);
        }
        finally
        {
            foreach (IDisposable lease in granted)
            {
                lease.Dispose();
            }
        }
    }

    [Fact]
    public void AViewIsStoppedByItsName()
    {
        MjpegStreamLimiter limiter = new(TestOptions.Monitor(new CameraOptions()));
        Guid view = Guid.NewGuid();

        using MjpegStreamLease? lease = limiter.TryAcquire(Alice, Camera, view);

        limiter.Stop(Alice, Camera, view).Should().BeTrue();
        lease!.Stopped.IsCancellationRequested.Should().BeTrue("the stream relaying it is watching this token");
    }

    /// <summary>
    /// A view is found only by the account that opened it, for the camera it is of - anything else is
    /// the same "no such view" as a name never used.
    /// </summary>
    [Fact]
    public void OnlyItsOwnAccountFindsAView()
    {
        MjpegStreamLimiter limiter = new(TestOptions.Monitor(new CameraOptions()));
        Guid view = Guid.NewGuid();

        using MjpegStreamLease? lease = limiter.TryAcquire(Alice, Camera, view);

        limiter.Stop(Bob, Camera, view).Should().BeFalse("another account's view is not found");
        limiter.Stop(Alice, Guid.NewGuid(), view).Should().BeFalse("nor is it under another camera");
        limiter.Stop(Alice, Camera, Guid.NewGuid()).Should().BeFalse("nor under a name never used");
        lease!.Stopped.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void AViewThatHasEndedIsNotFound()
    {
        MjpegStreamLimiter limiter = new(TestOptions.Monitor(new CameraOptions()));
        Guid view = Guid.NewGuid();

        limiter.TryAcquire(Alice, Camera, view)!.Dispose();

        limiter.Stop(Alice, Camera, view).Should().BeFalse("a view that ended has nothing left to stop");
    }

    /// <summary>
    /// A name already in use is not taken over: the second stream counts, and stopping the name stops
    /// the first, which holds it.
    /// </summary>
    [Fact]
    public void ANameInUseStaysWithItsFirstHolder()
    {
        MjpegStreamLimiter limiter = new(TestOptions.Monitor(new CameraOptions()));
        Guid view = Guid.NewGuid();

        using MjpegStreamLease? first = limiter.TryAcquire(Alice, Camera, view);
        using MjpegStreamLease? second = limiter.TryAcquire(Alice, Camera, view);

        second.Should().NotBeNull("it is still a stream the account may open");

        limiter.Stop(Alice, Camera, view).Should().BeTrue();
        first!.Stopped.IsCancellationRequested.Should().BeTrue();
        second!.Stopped.IsCancellationRequested.Should().BeFalse();
    }
}
