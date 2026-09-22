using System;

namespace Homespool.FakePrinter;

/// <summary>
/// Generates telemetry from the device's live state - our invention, clearly labelled as such:
/// shapes come from
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

    /// <summary>The materials the last message reported, to tell whether they have changed since.</summary>
    private string? _lastMaterials;

    /// <summary>Delay while not printing. Firmware: 15 s.</summary>
    public TimeSpan IdleInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Delay while printing. Firmware: 5 s (change-triggered sends floor at 2 s).</summary>
    public TimeSpan PrintingInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Every N-th message is the full shape; the rest are slim. The first is always full.</summary>
    public int FullShapeEvery { get; init; } = 5;

    /// <summary>
    /// The least time between a send and one a change on the device brings forward. Firmware: 2 s
    /// (<c>TELEMETRY_INTERVAL_MIN</c> for the non-iX build, planner.cpp). Null waits out the interval
    /// regardless.
    /// </summary>
    public TimeSpan? ChangeInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The analog values reported; replace to script temperatures etc.</summary>
    public TelemetryReadings Readings { get; set; } = new();

    /// <inheritdoc/>
    public byte[]? NextMessage(FakeDevice device)
    {
        // A change sends the full shape, as firmware's want_full = changes does: the slim one has no
        // material field, so a slim message after an unload would report nothing about it.
        string materials = MaterialsOf(device);
        bool changed = _lastMaterials is not null && materials != _lastMaterials;
        _lastMaterials = materials;

        bool full = _sent == 0 || changed || (FullShapeEvery > 0 && _sent % FullShapeEvery == 0);
        _sent++;

        return full ? TelemetryMessageBuilder.BuildFull(device, Readings) : TelemetryMessageBuilder.BuildSlim(device, Readings);
    }

    private string MaterialsOf(FakeDevice device)
    {
        string[] materials = new string[Math.Max(Readings.Tools, 1)];

        for (int tool = 1; tool <= materials.Length; tool++)
        {
            materials[tool - 1] = device.WireMaterialOf(tool);
        }

        return string.Join('\n', materials);
    }

    /// <inheritdoc/>
    public TimeSpan DelayBeforeNext(FakeDevice device)
    {
        return device.State == DeviceState.Printing ? PrintingInterval : IdleInterval;
    }
}
