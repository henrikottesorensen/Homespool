using System;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.Extensions.Time.Testing;

namespace Homespool.FakePrinter.Test;

/// <summary>
/// The clock-based event source: one event per interval whatever the telemetry rate underneath.
/// </summary>
/// <remarks>
/// This exists because a fixed ratio scales with the send rate, and the rate a blast runs at is
/// absurd - one event in ten of ~142,000 messages a second is tens of thousands of events a second.
/// Its sibling <see cref="EventMixingTelemetrySource"/> keeps the ratio, which is what the writer's
/// buffer-ceiling ordering rests on; this keeps the cadence, which is what a burst wants. Driven by
/// <see cref="FakeTimeProvider"/> so the intervals are asserted rather than waited for.
/// </remarks>
/// <remarks>
/// One thing this clock cannot reproduce, worth knowing rather than losing: it starts at a real date,
/// so <c>GetTimestamp</c> never returns zero. An earlier draft of the source under test used a zero
/// timestamp as its "no event yet" sentinel, which a hand-rolled clock starting at zero caught
/// immediately and this one would not have - zero is a legitimate reading from a caller's clock. The
/// source uses an explicit flag now, so the hazard is structural rather than defended, but the next
/// sentinel of that shape will need the same thought.
/// </remarks>
public class TimedEventTelemetrySourceTests
{
    private sealed class TelemetryOnlySource : ITelemetrySource
    {
        public byte[]? NextMessage(FakeDevice device) =>
            System.Text.Encoding.UTF8.GetBytes("""{"state":"IDLE"}""");

        public TimeSpan DelayBeforeNext(FakeDevice device) => TimeSpan.Zero;
    }

    private static bool IsEvent(byte[]? message)
    {
        using JsonDocument document = JsonDocument.Parse(message!);

        return document.RootElement.TryGetProperty("event", out _);
    }

    /// <summary>
    /// The first message is never an event, however the clock stands: the client opens a connection
    /// with INFO, and a state change before any telemetry established the state is a shape no printer
    /// produces. The interval is counted from that first message.
    /// </summary>
    [Fact]
    public void TheFirstMessageIsAlwaysTelemetry()
    {
        FakeTimeProvider clock = new();
        TimedEventTelemetrySource source = new(new TelemetryOnlySource(), clock)
        {
            EventInterval = TimeSpan.FromSeconds(10),
        };

        IsEvent(source.NextMessage(new FakeDevice())).Should().BeFalse();
    }

    /// <summary>
    /// However many messages pass inside one interval, none of them becomes an event - which is the
    /// whole difference from the ratio-based source, and the reason this one suits a blast.
    /// </summary>
    [Fact]
    public void NoEventArrivesBeforeTheIntervalHasElapsed()
    {
        FakeTimeProvider clock = new();
        TimedEventTelemetrySource source = new(new TelemetryOnlySource(), clock)
        {
            EventInterval = TimeSpan.FromSeconds(10),
        };
        FakeDevice device = new();

        source.NextMessage(device);
        clock.Advance(TimeSpan.FromSeconds(9));

        int events = 0;

        for (int i = 0; i < 10_000; i++)
        {
            if (IsEvent(source.NextMessage(device)))
            {
                events++;
            }
        }

        events.Should().Be(0, "ten thousand messages inside one interval are still no occasion for an event");
    }

    /// <summary>One event per elapsed interval, and exactly one - not a burst once the clock passes it.</summary>
    [Fact]
    public void OneEventArrivesPerElapsedInterval()
    {
        FakeTimeProvider clock = new();
        TimedEventTelemetrySource source = new(new TelemetryOnlySource(), clock)
        {
            EventInterval = TimeSpan.FromSeconds(10),
        };
        FakeDevice device = new();

        source.NextMessage(device);

        int events = 0;

        for (int interval = 0; interval < 3; interval++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));

            // A hundred messages per interval: the first past the deadline is the event, the rest are
            // telemetry again.
            for (int i = 0; i < 100; i++)
            {
                if (IsEvent(source.NextMessage(device)))
                {
                    events++;
                }
            }
        }

        events.Should().Be(3, "one event per interval regardless of how many messages carried it");
    }

    /// <summary>A non-positive interval is a pass-through, so the option is off unless asked for.</summary>
    [Fact]
    public void ANonPositiveIntervalLeavesTheStreamAlone()
    {
        FakeTimeProvider clock = new();
        TimedEventTelemetrySource source = new(new TelemetryOnlySource(), clock)
        {
            EventInterval = TimeSpan.Zero,
        };
        FakeDevice device = new();

        clock.Advance(TimeSpan.FromHours(1));

        IsEvent(source.NextMessage(device)).Should().BeFalse();
    }
}
