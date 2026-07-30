using System;

namespace Homespool.FakePrinter;

/// <summary>
/// Wraps another <see cref="ITelemetrySource"/> and substitutes a <c>STATE_CHANGED</c> event once per
/// wall-clock interval, whatever rate the telemetry underneath is running at.
/// </summary>
/// <remarks>
/// <para>
/// <b>The counterpart to <see cref="EventMixingTelemetrySource"/>, and the right one for a burst.</b>
/// Substituting every N-th message keeps a fixed <em>ratio</em>, which is what the writer's buffer
/// ceilings care about - the ordering claim that events outlive samples rests on the stream ratio, so
/// a rig measuring that wants the count. But a ratio scales with the send rate: at blast speed, one
/// event in ten of ~142,000 messages a second is roughly 14,000 events a second, which no printer
/// resembles even slightly. Events happen when something happens, so a burst wants them pinned to the
/// clock instead: telemetry floods, events tick.
/// </para>
/// <para>
/// A rig instrument either way, per mitigation #3 in <c>notes/fake-printer-harness.md</c>: firmware
/// emits events on occasions, not on a timer.
/// </para>
/// <para>
/// Substitutes rather than inserts, like its sibling, because the client's loop takes one message per
/// call. At the rates this is meant for the displaced telemetry message is a rounding error; at
/// human-scale intervals it is one message per interval.
/// </para>
/// </remarks>
public sealed class TimedEventTelemetrySource : ITelemetrySource
{
    private readonly ITelemetrySource _inner;
    private readonly TimeProvider _timeProvider;

    private long _lastEventTimestamp;
    private bool _started;

    /// <summary>Wraps <paramref name="inner"/>, substituting timed events into what it produces.</summary>
    /// <param name="inner">The source supplying every message this one does not replace.</param>
    /// <param name="timeProvider">
    /// The clock. Taken from the caller rather than read statically, so a test can drive the interval
    /// without waiting for it.
    /// </param>
    public TimedEventTelemetrySource(ITelemetrySource inner, TimeProvider timeProvider)
    {
        _inner = inner;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// How long between events. Intervals of zero or less disable substitution, leaving the inner
    /// source untouched.
    /// </summary>
    public TimeSpan EventInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <inheritdoc/>
    public byte[]? NextMessage(FakeDevice device)
    {
        if (EventInterval <= TimeSpan.Zero)
        {
            return _inner.NextMessage(device);
        }

        long now = _timeProvider.GetTimestamp();

        // A bool rather than testing the timestamp against zero: zero is a legitimate reading from a
        // clock a caller supplies, and Stopwatch-based ones only avoid it by luck. Sentinel values
        // that a real source can produce are how "unset" quietly becomes "due".
        if (!_started)
        {
            // The clock starts at the first message rather than at construction, so the first event
            // lands one whole interval into the run. Never on the first message: the client opens a
            // connection with INFO, and a state change before any telemetry has established what the
            // state was is a shape no printer produces.
            _started = true;
            _lastEventTimestamp = now;

            return _inner.NextMessage(device);
        }

        if (_timeProvider.GetElapsedTime(_lastEventTimestamp, now) < EventInterval)
        {
            return _inner.NextMessage(device);
        }

        _lastEventTimestamp = now;

        return EventMessageBuilder.Build("STATE_CHANGED", device.WireState);
    }

    /// <inheritdoc/>
    public TimeSpan DelayBeforeNext(FakeDevice device)
    {
        return _inner.DelayBeforeNext(device);
    }
}
