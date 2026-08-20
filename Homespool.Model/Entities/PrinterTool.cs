namespace Homespool.Model.Entities;

/// <summary>
/// The hardware fitted to one of a printer's tools, as the printer reports it in <c>INFO</c>:
/// one row per tool, upserted whenever an <c>INFO</c> carries a <c>tools</c> block.
/// </summary>
/// <remarks>
/// <para>
/// <b>Capability, not telemetry</b> - which is the whole reason it is not
/// <see cref="PrinterLiveSlotState"/>. That table carries what a slot is doing right now
/// (temperature, fan speed) and is written from the telemetry stream; this carries what the machine
/// <i>is</i>, and changes only when somebody changes a nozzle. The wire keeps them apart too: they
/// are different objects with different shapes and the same numbering.
/// </para>
/// <para>
/// <b>It exists because <see cref="Printer.NozzleDiameter"/> cannot answer two of the four
/// questions.</b> <c>INFO</c> reports hardened and high-flow <i>only</i> per tool - there is no
/// top-level form of either - so before this table those two values were parsed into
/// <c>InfoToolDTO</c> and dropped on the floor, and nothing could tell whether an abrasive filament
/// was about to be pushed through a soft nozzle.
/// </para>
/// <para>
/// <b>Unreported tools are left alone rather than deleted.</b> Absence on this wire means "not
/// said", not "not there" - the same rule that stops a partial <c>INFO</c> clearing
/// <see cref="Printer.HasMmuEnabled"/>. A tool that genuinely goes away leaves a stale row, which
/// costs nothing: every comparison starts from what the file needs and looks up the tool it names.
/// </para>
/// </remarks>
public class PrinterTool
{
    public int PrinterId { get; set; }

    /// <summary>1-based tool number, exactly as keyed on the wire.</summary>
    /// <remarks>
    /// A single-tool printer reports one entry numbered <c>1</c>, so a MK4 or a CORE One has exactly
    /// one row here and it is the one every check consults.
    /// </remarks>
    public int ToolNumber { get; set; }

    /// <summary>Fitted nozzle diameter in millimetres, or null if the printer reported zero.</summary>
    /// <remarks>
    /// Zero is treated as unreported, matching <see cref="Printer.NozzleDiameter"/>: a literal
    /// 0.0 mm nozzle does not exist.
    /// </remarks>
    public float? NozzleDiameter { get; set; }

    /// <summary>
    /// Whether this tool's nozzle is hardened, and so may be used with abrasive filament.
    /// </summary>
    /// <remarks>
    /// <b>A setting somebody maintains, not a measurement</b> - exactly like the diameter beside it,
    /// and the reason neither is second-guessed. Firmware's own renderer emits it unconditionally
    /// inside every tool object, so a plain <c>bool</c> is faithful here where the MMU block's
    /// tri-state is not.
    /// </remarks>
    public bool Hardened { get; set; }

    /// <summary>Whether this tool's hotend is a high-flow one.</summary>
    public bool HighFlow { get; set; }

    /// <summary>
    /// The material the printer says is loaded, or null when it reported the <c>"---"</c> sentinel
    /// for none.
    /// </summary>
    public string? Material { get; set; }
}
