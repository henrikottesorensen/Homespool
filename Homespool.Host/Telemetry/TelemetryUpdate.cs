using System.Collections.Generic;

using Homespool.Model;

namespace Homespool.Host.Telemetry;

/// <summary>
/// What one telemetry message said about a printer, in Homespool's vocabulary and units - the
/// currency every protocol edge converts into and the only telemetry shape
/// <see cref="ITelemetrySink"/> accepts. Each cell is a <see cref="Field{T}"/>, so the edge states
/// its merge policy per field - coalesce, authoritative value, or clear - and
/// <see cref="PrinterLiveStateMerger"/> applies it mechanically with no protocol knowledge at all.
/// </summary>
public sealed record TelemetryUpdate
{
    /// <summary>Printer state, already mapped into the domain vocabulary by the edge. Every
    /// protocol's telemetry carries one, so it is required rather than a cell.</summary>
    public required PrinterStatus Status { get; init; }

    /// <summary>The running job's id. Job-scoped: an edge clears the whole job block with
    /// <see cref="Field{T}.Null"/> when its protocol says the job is gone.</summary>
    public Field<int?> JobId { get; init; }

    /// <summary>Job progress, percent. Job-scoped as <see cref="JobId"/>.</summary>
    public Field<int?> Progress { get; init; }

    /// <summary>Seconds spent printing. Job-scoped as <see cref="JobId"/>.</summary>
    public Field<int?> TimePrinting { get; init; }

    /// <summary>Estimated seconds remaining. Job-scoped as <see cref="JobId"/>.</summary>
    public Field<int?> TimeRemaining { get; init; }

    /// <summary>Seconds until a filament change (<c>M600</c>). Job-scoped as <see cref="JobId"/>.</summary>
    public Field<int?> TimeToFilamentChange { get; init; }

    /// <summary>Extruder/hotend fan speed.</summary>
    public Field<int?> ExtruderFan { get; init; }

    /// <summary>Print/part-cooling fan speed.</summary>
    public Field<int?> PrintFan { get; init; }

    /// <summary>Lifetime filament odometer - deliberately not job-scoped; a figure that stops
    /// rising when nothing extrudes is correct where a frozen progress is a lie.</summary>
    public Field<float?> FilamentUsed { get; init; }

    /// <summary>Nozzle temperature.</summary>
    public Field<float?> NozzleTemperature { get; init; }

    /// <summary>Bed temperature.</summary>
    public Field<float?> BedTemperature { get; init; }

    /// <summary>Nozzle target.</summary>
    public Field<float?> TargetNozzleTemperature { get; init; }

    /// <summary>Bed target.</summary>
    public Field<float?> TargetBedTemperature { get; init; }

    /// <summary>Speed factor, percent.</summary>
    public Field<int?> Speed { get; init; }

    /// <summary>Flow factor, percent.</summary>
    public Field<int?> Flow { get; init; }

    /// <summary>Loaded material, e.g. <c>PETG</c>.</summary>
    public Field<string?> Material { get; init; }

    /// <summary>X position.</summary>
    public Field<float?> XAxis { get; init; }

    /// <summary>Y position.</summary>
    public Field<float?> YAxis { get; init; }

    /// <summary>Z height.</summary>
    public Field<float?> ZAxis { get; init; }

    /// <summary>Heatbreak temperature.</summary>
    public Field<float?> HeatbreakTemperature { get; init; }

    /// <summary>PSU temperature.</summary>
    public Field<float?> PsuTemperature { get; init; }

    /// <summary>Ambient temperature.</summary>
    public Field<float?> AmbientTemperature { get; init; }

    /// <summary>Extruder filament sensor state.</summary>
    public Field<string?> ExtruderFilamentSensorStatus { get; init; }

    /// <summary>Remote (e.g. MMU) filament sensor state.</summary>
    public Field<string?> RemoteFilamentSensorStatus { get; init; }

    /// <summary>Chamber temperature.</summary>
    public Field<float?> ChamberTemperature { get; init; }

    /// <summary>Chamber target temperature.</summary>
    public Field<int?> ChamberTargetTemperature { get; init; }

    /// <summary>Chamber fan 1 speed.</summary>
    public Field<int?> ChamberFan1Rpm { get; init; }

    /// <summary>Chamber fan 2 speed.</summary>
    public Field<int?> ChamberFan2Rpm { get; init; }

    /// <summary>Chamber fan PWM target.</summary>
    public Field<int?> ChamberFanPwmTarget { get; init; }

    /// <summary>Chamber LED intensity.</summary>
    public Field<int?> ChamberLedIntensity { get; init; }

    /// <summary>Enclosure temperature.</summary>
    public Field<int?> EnclosureTemperature { get; init; }

    /// <summary>Enclosure fan speed.</summary>
    public Field<int?> EnclosureFanRpm { get; init; }

    /// <summary>Enclosure filter time in use.</summary>
    public Field<int?> EnclosureTimeInUse { get; init; }

    /// <summary>The active tool/MMU slot.</summary>
    public Field<int?> ActiveSlot { get; init; }

    /// <summary>MMU state.</summary>
    public Field<int?> MmuState { get; init; }

    /// <summary>The MMU's current command.</summary>
    public Field<string?> MmuCommand { get; init; }

    /// <summary>Per-slot reports; a slot not listed is untouched. See <see cref="SlotUpdate"/>.</summary>
    public IReadOnlyList<SlotUpdate> Slots { get; init; } = [];
}
