using System;
using System.Threading.Tasks;

using AwesomeAssertions;

using Homespool.Host.Services;

namespace Homespool.Host.Test;

/// <summary>
/// The election-and-count primitive behind every throttled wire-rate log site. The contract under
/// test: first occurrence elected immediately, at most one election per interval after that, and
/// the lifetime total exact whatever the logging did - see <c>LogThrottle</c>'s remarks for the
/// 1.0 GB blast log that motivated it.
/// </summary>
public class LogThrottleTests
{
    /// <summary>The very first occurrence is elected, flagged as first, with count and total 1.</summary>
    [Fact]
    public void TheFirstOccurrenceIsElectedImmediately()
    {
        LogThrottle throttle = new(TimeSpan.FromMinutes(10));

        LogThrottleWindow? window = throttle.Record();

        window.Should().NotBeNull();
        window!.Value.IsFirstOccurrence.Should().BeTrue();
        window.Value.Count.Should().Be(1);
        window.Value.Total.Should().Be(1);
    }

    /// <summary>A burst inside the interval yields exactly one election - the flood protection itself.</summary>
    [Fact]
    public void ABurstInsideTheIntervalElectsOnlyTheFirst()
    {
        LogThrottle throttle = new(TimeSpan.FromMinutes(10));
        int elected = 0;

        for (int i = 0; i < 1000; i++)
        {
            if (throttle.Record() is not null)
            {
                elected++;
            }
        }

        elected.Should().Be(1);
        throttle.Total.Should().Be(1000, "every occurrence is counted even when not logged");
    }

    /// <summary>
    /// The first occurrence past the interval is elected and carries everything accumulated since
    /// the previous election - deferred, never lost.
    /// </summary>
    [Fact]
    public async Task TheFirstOccurrencePastTheIntervalCarriesTheAccumulatedCount()
    {
        LogThrottle throttle = new(TimeSpan.FromMilliseconds(100));

        for (int i = 0; i < 10; i++)
        {
            throttle.Record();
        }

        await Task.Delay(150);

        LogThrottleWindow? summary = throttle.Record();

        summary.Should().NotBeNull();
        summary!.Value.IsFirstOccurrence.Should().BeFalse();
        summary.Value.Count.Should().Be(10, "the 9 silent occurrences ride along with this one");
        summary.Value.Total.Should().Be(11);
        summary.Value.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(100));
    }

    /// <summary>
    /// Concurrent recorders never lose a count and never double-elect - the drop callback fires on
    /// whatever producer thread hit the full channel, so this is the deployment shape, not an edge.
    /// </summary>
    [Fact]
    public async Task ConcurrentRecordersCountExactlyAndElectAtMostOnePerWindow()
    {
        LogThrottle throttle = new(TimeSpan.FromMinutes(10));
        int elected = 0;

        Task[] recorders = new Task[8];

        for (int t = 0; t < recorders.Length; t++)
        {
            recorders[t] = Task.Run(() =>
            {
                for (int i = 0; i < 10_000; i++)
                {
                    if (throttle.Record() is not null)
                    {
                        System.Threading.Interlocked.Increment(ref elected);
                    }
                }
            });
        }

        await Task.WhenAll(recorders);

        throttle.Total.Should().Be(80_000, "the lifetime total is exact under contention");
        elected.Should().Be(1, "one window, one winner, however many threads race");
    }
}
