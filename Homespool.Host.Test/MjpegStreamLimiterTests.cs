using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Homespool.Host.Cameras;

namespace Homespool.Host.Test;

/// <summary>
/// The per-account ceiling on live MJPEG streams: what it allows, what it refuses, and that a stream
/// ending gives its place back.
/// </summary>
public class MjpegStreamLimiterTests
{
    private const long Alice = 1;
    private const long Bob = 2;

    [Fact]
    public void TheDefaultIsFive()
    {
        new CameraOptions().MaxMjpegStreamsPerUser.Should().Be(5);
    }

    [Fact]
    public void AnAccountMayOpenUpToTheLimitAndNoMore()
    {
        MjpegStreamLimiter limiter = new(TestOptions.Monitor(new CameraOptions()));

        IDisposable?[] open = [.. Enumerable.Range(0, 5).Select(_ => limiter.TryAcquire(Alice))];

        try
        {
            open.Should().NotContainNulls("five is the default");

            limiter.TryAcquire(Alice).Should().BeNull("the sixth is one past it");
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

        IDisposable? first = limiter.TryAcquire(Alice);
        first.Should().NotBeNull();
        limiter.TryAcquire(Alice).Should().BeNull();

        first!.Dispose();

        using IDisposable? again = limiter.TryAcquire(Alice);
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

        using IDisposable? alice = limiter.TryAcquire(Alice);
        using IDisposable? bob = limiter.TryAcquire(Bob);

        alice.Should().NotBeNull();
        bob.Should().NotBeNull();
    }

    [Fact]
    public void DisposingALeaseTwiceGivesBackOneStreamNotTwo()
    {
        MjpegStreamLimiter limiter = new(TestOptions.Monitor(new CameraOptions { MaxMjpegStreamsPerUser = 2 }));

        IDisposable? first = limiter.TryAcquire(Alice);
        using IDisposable? second = limiter.TryAcquire(Alice);

        first!.Dispose();
        first.Dispose();

        using IDisposable? third = limiter.TryAcquire(Alice);
        third.Should().NotBeNull("the first lease's place came back once");
        limiter.TryAcquire(Alice).Should().BeNull("and only once, so two are open again");
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

        using IDisposable? first = limiter.TryAcquire(Alice);
        using IDisposable? second = limiter.TryAcquire(Alice);

        options.Set(new CameraOptions { MaxMjpegStreamsPerUser = 2 });

        limiter.TryAcquire(Alice).Should().BeNull("two are open and two is now the limit");
    }

    [Fact]
    public async Task ConcurrentOpensNeverExceedTheLimit()
    {
        MjpegStreamLimiter limiter = new(TestOptions.Monitor(new CameraOptions()));
        ConcurrentBag<IDisposable> granted = [];

        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() =>
        {
            if (limiter.TryAcquire(Alice) is { } lease)
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
}
