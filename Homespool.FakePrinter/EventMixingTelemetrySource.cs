using System;

namespace Homespool.FakePrinter;

/// <summary>
/// Wraps another <see cref="ITelemetrySource"/> and substitutes a <c>STATE_CHANGED</c> event for
/// every N-th message, so a load run exercises the event path as well as the telemetry one.
/// </summary>
/// <remarks>
/// <para>
/// <b>A rig instrument, not firmware-faithful</b> - the same labelling that applies to
/// <see cref="SyntheticTelemetrySource"/>. Firmware
/// emits events <em>in addition to</em> telemetry, driven by things actually happening; this
/// substitutes one for a telemetry message on a fixed count, which no printer does. What it
/// reproduces faithfully is the only property the writer's buffers care about: the <em>ratio</em>
/// between the two streams. <see cref="EventEvery"/> defaults to 10 because that is the ratio
/// <c>TelemetryWriter</c>'s <c>MaxPendingEventBatches</c> remark reasons from - "a printing printer
/// emits telemetry roughly ten times as often as it emits events".
/// </para>
/// <para>
/// <c>STATE_CHANGED</c> is the event chosen because it is a real, plain one: no <c>data</c> block, no
/// command id, nothing that would make the server treat it as an answer to something it never sent.
/// It carries the device's current wire state, exactly as it does on a real connection.
/// </para>
/// </remarks>
public sealed class EventMixingTelemetrySource : ITelemetrySource
{
    private readonly ITelemetrySource _inner;

    private int _sent;

    /// <summary>Wraps <paramref name="inner"/>, substituting events into the stream it produces.</summary>
    /// <param name="inner">The telemetry source that supplies every message this one does not replace.</param>
    public EventMixingTelemetrySource(ITelemetrySource inner)
    {
        _inner = inner;
    }

    /// <summary>
    /// Every N-th message is an event instead of telemetry. 1 makes every message an event; values
    /// below 1 disable substitution entirely, leaving the inner source untouched.
    /// </summary>
    public int EventEvery { get; init; } = 10;

    /// <inheritdoc/>
    public byte[]? NextMessage(FakeDevice device)
    {
        _sent++;

        // Never on the first message: the client opens a connection with INFO, and following it
        // immediately with a state change - before any telemetry has established what the state was -
        // is a shape no printer produces.
        if (EventEvery > 0 && _sent > 1 && _sent % EventEvery == 0)
        {
            return EventMessageBuilder.Build("STATE_CHANGED", device.WireState);
        }

        return _inner.NextMessage(device);
    }

    /// <inheritdoc/>
    public TimeSpan DelayBeforeNext(FakeDevice device)
    {
        return _inner.DelayBeforeNext(device);
    }
}
