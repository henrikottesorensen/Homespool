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

    /// <summary>The job the warm-up is being counted for, so a new print starts its own.</summary>
    private int? _countedJob;

    /// <summary>Time spent actually printing on <see cref="_countedJob"/> - a pause does not count.</summary>
    private TimeSpan _printedFor;

    /// <summary>When the last message was built, for the time elapsed since.</summary>
    private DateTimeOffset? _lastMessageAt;

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

    /// <summary>
    /// How long a print homes, probes and heats before it extrudes anything. Measured at 168 s on an
    /// MK3.5 from cold; shorter here so a development run shows the split without the wait.
    /// </summary>
    public TimeSpan WarmUp { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Millimetres of filament extruded per second of printing once the warm-up is over.</summary>
    public double ExtrusionRate { get; init; } = 1.5;

    /// <summary>What <see cref="WarmUp"/> and <see cref="ExtrusionRate"/> are measured against.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <inheritdoc/>
    public byte[]? NextMessage(FakeDevice device)
    {
        Advance(device);

        // A change sends the full shape, as firmware's want_full = changes does: the slim one has no
        // material field, so a slim message after an unload would report nothing about it.
        string materials = MaterialsOf(device);
        bool changed = _lastMaterials is not null && materials != _lastMaterials;
        _lastMaterials = materials;

        bool full = _sent == 0 || changed || (FullShapeEvery > 0 && _sent % FullShapeEvery == 0);
        _sent++;

        return full ? TelemetryMessageBuilder.BuildFull(device, Readings) : TelemetryMessageBuilder.BuildSlim(device, Readings);
    }

    /// <summary>
    /// Moves the device's filament odometer on by whatever the print extruded since the last message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing for the first <see cref="WarmUp"/> of each print</b>, which is the finding this
    /// exists to reproduce: a printer reports <c>PRINTING</c> with the nozzle cold, and the odometer
    /// first rising is the only sign that plastic has started to move.
    /// </para>
    /// <para>
    /// <b>Our invention, and simpler than the machine in one way worth knowing</b>: firmware sends a
    /// full message whenever the odometer crosses another 10 mm, and this one keeps to its own
    /// cadence, so the figure reaches the server only as often as <see cref="FullShapeEvery"/> allows.
    /// </para>
    /// </remarks>
    private void Advance(FakeDevice device)
    {
        DateTimeOffset now = Clock.GetUtcNow();
        TimeSpan elapsed = _lastMessageAt is { } last && now > last ? now - last : TimeSpan.Zero;
        _lastMessageAt = now;

        if (device.JobId != _countedJob)
        {
            _countedJob = device.JobId;
            _printedFor = TimeSpan.Zero;
        }

        if (device.State != DeviceState.Printing)
        {
            return;
        }

        TimeSpan before = _printedFor;
        _printedFor += elapsed;

        TimeSpan extruding = Past(_printedFor) - Past(before);

        if (extruding > TimeSpan.Zero)
        {
            device.Extrude(extruding.TotalSeconds * ExtrusionRate);
        }
    }

    private TimeSpan Past(TimeSpan printed)
    {
        return printed > WarmUp ? printed - WarmUp : TimeSpan.Zero;
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
