namespace Homespool.Model.Entities;

/// <summary>
/// Last-known per-slot values for one printer: one row per slot, upserted alongside
/// <see cref="PrinterLiveState"/>. Never grows.
/// </summary>
/// <remarks>
/// <para>
/// The live-state counterpart to <see cref="TelemetrySlotSample"/>. <see cref="PrinterLiveState"/>
/// is a single row per printer and so cannot carry per-slot data itself.
/// </para>
/// <para>
/// Keyed by (<see cref="PrinterId"/>, <see cref="SlotNumber"/>) — a natural composite key that
/// makes the merge a straightforward upsert.
/// </para>
/// <para>
/// Fields are nullable and merged the same way as the parent: a reduced telemetry message omits
/// the <c>slot</c> object entirely and must not blank out the last-known values.
/// </para>
/// </remarks>
public class PrinterLiveSlotState
{
    public int PrinterId { get; set; }

    public virtual PrinterLiveState? PrinterLiveState { get; set; }

    /// <summary>1-based slot number, exactly as keyed on the wire.</summary>
    public int SlotNumber { get; set; }

    public string? Material { get; set; }

    /// <summary>Nozzle temperature for this slot.</summary>
    public float? Temperature { get; set; }

    /// <summary>Hotend (heatbreak) fan speed in RPM; arrives as a float. See TelemetrySlotSample.</summary>
    public float? HotendFanRpm { get; set; }

    /// <summary>Print fan speed in RPM; arrives as a float.</summary>
    public float? PrintFanRpm { get; set; }
}
