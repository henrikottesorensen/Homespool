using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Homespool.Model.Entities;

/// <summary>
/// Live per-slot values for one <see cref="TelemetrySample"/> on a multi-tool printer.
/// </summary>
/// <remarks>
/// <para>
/// Derived from Buddy's <c>render.cpp</c>, which emits a <c>"slot"</c> object in telemetry —
/// <b>not</b> <c>"tools"</c>. <c>"tools"</c> is a different object carrying static per-slot
/// configuration, and it only appears in the <c>INFO</c> event. Do not conflate them.
/// </para>
/// <para>
/// The firmware emits <c>slot</c> only when <c>enabled_tool_cnt() &gt; 1</c>, so single-tool
/// printers produce no rows here at all. Slot numbers are <b>1-based</b> on the wire.
/// </para>
/// <para>
/// A child table rather than columns or JSON: tool counts are genuinely open-ended — XL has 5,
/// and the INDX upgrades push the Core One to 8 and the Core One L to as many as 10. Buddy's own
/// comment notes INDX "has a lot of physical tools".
/// </para>
/// <para>
/// Rows are removed by database-level cascade when the parent sample is swept by retention, which
/// relies on <c>PRAGMA foreign_keys = ON</c> — set by <c>SqlitePragmaInterceptor</c>.
/// </para>
/// </remarks>
public class TelemetrySlotSample
{
    [Key]
    public long Id { get; set; }

    public long TelemetrySampleId { get; set; }

    [ForeignKey(nameof(TelemetrySampleId))]
    public virtual TelemetrySample? TelemetrySample { get; set; }

    /// <summary>1-based slot number, exactly as keyed on the wire.</summary>
    public int SlotNumber { get; set; }

    public string? Material { get; set; }

    /// <summary>Nozzle temperature for this slot.</summary>
    public float? Temperature { get; set; }

    /// <summary>
    /// Hotend (heatbreak) fan speed in RPM.
    /// </summary>
    /// <remarks>
    /// Typed as a float because the firmware renders it with <c>JSON_FIELD_FFIXED</c> — it arrives
    /// as <c>8185.0</c> — even though the underlying value is a <c>uint16_t</c>.
    /// </remarks>
    public float? HotendFanRpm { get; set; }

    /// <summary>Print fan speed in RPM. Rendered as a float, as above.</summary>
    public float? PrintFanRpm { get; set; }
}
