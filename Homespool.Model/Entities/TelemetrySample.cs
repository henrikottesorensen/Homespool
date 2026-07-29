using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Homespool.Model.Entities;

/// <summary>
/// Append-only telemetry history — the rows charts are drawn from.
/// </summary>
/// <remarks>
/// <para>
/// Samples are <b>dense</b>: each row is emitted from the merged <see cref="PrinterLiveState"/>
/// rather than from the raw message, so a slim wire message still produces a row with the
/// last-known temperatures carried forward. This costs some storage on unchanged fields and
/// buys a read path with no carry-forward, no interpolation and no window functions.
/// </para>
/// <para>
/// Rows are still nullable throughout, because a field the printer has never reported has no
/// last-known value to carry — a MINI has no chamber.
/// </para>
/// <para>
/// Subject to retention (default 14 days, configurable). See AGENT-NOTES §5.
/// </para>
/// </remarks>
public class TelemetrySample
{
    [Key]
    public long Id { get; set; }

    // Deliberately no Printer navigation property, only the FK. TelemetryWriter keeps these rows
    // buffered across failed flushes, and a navigation is a slot EF's relationship fix-up writes a
    // tracked Printer instance into during the failed attempt - which then collides with the next
    // context's own instance and wedges every flush thereafter (found by the slow-database rig,
    // 2026-07-29). No navigation, nothing to poison. Queries wanting printer fields join explicitly,
    // as PrinterWithState and PrinterStatistics already do.
    public int PrinterId { get; set; }

    /// <summary>When this sample was recorded by the server.</summary>
    public DateTimeOffset Timestamp { get; set; }

    // -- Core --------------------------------------------------------------------------------
    public PrinterStatus Status { get; set; }

    public int? JobId { get; set; }

    public int? Progress { get; set; }

    public int? TimePrinting { get; set; }

    public int? TimeRemaining { get; set; }

    // -- Temperatures and motion -------------------------------------------------------------
    public float? NozzleTemperature { get; set; }

    public float? BedTemperature { get; set; }

    public float? TargetNozzleTemperature { get; set; }

    public float? TargetBedTemperature { get; set; }

    public int? Speed { get; set; }

    public int? Flow { get; set; }

    public string? Material { get; set; }

    public float? XAxis { get; set; }

    public float? YAxis { get; set; }

    public float? ZAxis { get; set; }

    public int? ExtruderFan { get; set; }

    public int? PrintFan { get; set; }

    public float? FilamentUsed { get; set; }

    public int? TimeToFilamentChange { get; set; }

    // -- Chamber -----------------------------------------------------------------------------
    public float? ChamberTemperature { get; set; }

    public int? ChamberTargetTemperature { get; set; }

    public int? ChamberFan1Rpm { get; set; }

    public int? ChamberFan2Rpm { get; set; }

    public int? ChamberFanPwmTarget { get; set; }

    public int? ChamberLedIntensity { get; set; }

    // -- Enclosure ---------------------------------------------------------------------------
    //
    // XL only, and integers in the firmware. The telemetry "enclosure" object is a different shape
    // from the INFO event's object of the same name.
    public int? EnclosureTemperature { get; set; }

    public int? EnclosureFanRpm { get; set; }

    public int? EnclosureTimeInUse { get; set; }

    // -- Prusa iX ----------------------------------------------------------------------------
    public float? HeatbreakTemperature { get; set; }

    public float? PsuTemperature { get; set; }

    public float? AmbientTemperature { get; set; }

    // -- Slots (multi-tool printers only) -----------------------------------------------------
    //
    // Null on single-tool printers: the firmware emits "slot" only when enabled_tool_cnt() > 1.

    /// <summary>Which slot was active. Wire field <c>slot.active</c>, 1-based.</summary>
    public int? ActiveSlot { get; set; }

    /// <summary>MMU progress code. Wire field <c>slot.state</c>, MMU builds only.</summary>
    public int? MmuState { get; set; }

    /// <summary>MMU command code, a single character. Wire field <c>slot.command</c>.</summary>
    public string? MmuCommand { get; set; }

    /// <summary>Per-slot values for this sample. Empty on single-tool printers.</summary>
    public virtual ICollection<TelemetrySlotSample> Slots { get; set; } = [];
}
