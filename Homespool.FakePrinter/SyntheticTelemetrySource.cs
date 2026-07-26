using System;

namespace Homespool.FakePrinter;

/// <summary>
/// Generates telemetry from the device's live state - our invention, clearly labelled as such
/// (mitigation #3 in <c>notes/fake-printer-harness.md</c>): shapes come from
/// <see cref="TelemetryMessageBuilder"/>, cadence and the full/slim alternation mimic the firmware's
/// scheduler, but the values are synthesized.
/// </summary>
/// <remarks>
/// Cadence defaults are the 6.6.0 non-iX websocket constants (planner.cpp:91-111): 15 s idle
/// (<c>TELEMETRY_INTERVAL_LONG</c>), 5 s printing (<c>TELEMETRY_INTERVAL_SHORT</c>), and a full
/// shape at least every 5 minutes (<c>TELEMETRY_INTERVAL_FULL</c>) - here approximated as every
/// N-th message plus always the first. The real MK3.5 on 6.4.0 measured faster while printing;
/// override the intervals to taste.
/// </remarks>
public sealed class SyntheticTelemetrySource : ITelemetrySource
{
    private int _sent;

    /// <summary>Delay while not printing. Firmware: 15 s.</summary>
    public TimeSpan IdleInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Delay while printing. Firmware: 5 s (change-triggered sends floor at 2 s).</summary>
    public TimeSpan PrintingInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Every N-th message is the full shape; the rest are slim. The first is always full.</summary>
    public int FullShapeEvery { get; init; } = 5;

    /// <summary>The analog values reported; replace to script temperatures etc.</summary>
    public TelemetryReadings Readings { get; set; } = new();

    /// <inheritdoc/>
    public byte[]? NextMessage(FakeDevice device)
    {
        bool full = _sent == 0 || (FullShapeEvery > 0 && _sent % FullShapeEvery == 0);
        _sent++;

        return full
            ? TelemetryMessageBuilder.BuildFull(device, Readings)
            : TelemetryMessageBuilder.BuildSlim(device, Readings);
    }

    /// <inheritdoc/>
    public TimeSpan DelayBeforeNext(FakeDevice device)
    {
        return device.State == DeviceState.Printing ? PrintingInterval : IdleInterval;
    }
}
