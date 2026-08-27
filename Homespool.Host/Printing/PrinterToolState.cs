namespace Homespool.Host.Printing;

/// <summary>
/// One tool a printer has told us about - what is in it, how hot it is, and whether it is the one
/// currently on the carriage.
/// </summary>
/// <remarks>
/// <para>
/// <b>A single-tool printer produces exactly one of these too</b>, synthesised from the flat
/// telemetry fields, so a caller never branches on how many heads a machine has. Firmware sends a
/// slot block only when <c>enabled_tool_cnt() &gt; 1</c> (<c>render.cpp:230</c>), and this is where
/// that asymmetry stops.
/// </para>
/// <para>
/// <b><see cref="ToolNumber"/> is the wire's, 1-based</b>, and gcode's <c>T</c> is 0-based -
/// <see cref="PrusaConnect.Commands.UnloadFilament.ForTool"/> is the one place that converts.
/// </para>
/// </remarks>
/// <param name="ToolNumber">As telemetry and <c>INFO</c> number it, 1-based, and <b>not necessarily
/// contiguous</b>: a machine may report 1, 2, 4 and 8.</param>
/// <param name="Material">What is loaded, or null for an empty tool - already free of the
/// <c>"---"</c> sentinel.</param>
/// <param name="Temperature">The tool's own nozzle reading, where it reports one.</param>
/// <param name="IsPicked">
/// Whether this is the tool on the carriage. <b>Meaningful only on a toolchanger</b>, and false
/// throughout on a single-tool printer, which sends no slot block to say so.
/// </param>
/// <param name="NozzleDiameter">
/// From <c>INFO</c>, where the printer describes each tool. Null when it has not said.
/// </param>
/// <param name="Hardened">
/// Whether this tool's nozzle is hardened - <b>not decoration</b>. <c>QueueRules</c> holds an
/// abrasive print when the printer reports no hardened nozzle, and on a toolchanger the useful
/// question is <em>which head</em>, which nothing on the page could answer before this.
/// </param>
public sealed record PrinterToolState(int ToolNumber,
                                      string? Material,
                                      float? Temperature,
                                      bool IsPicked,
                                      float? NozzleDiameter = null,
                                      bool Hardened = false)
{
    /// <summary>
    /// Whether this tool can be unloaded from here.
    /// </summary>
    /// <remarks>
    /// <b>Keyed on the material, and that is a firmware precondition rather than a nicety.</b> With
    /// no stored filament type <c>M702</c> falls into <c>preheatTempUnKnown</c> and blocks on a dialog
    /// at the panel. Being picked has nothing to do with it:
    /// <c>M702_unload</c> changes to the target tool itself (<c>M701_2.cpp:168-171</c>).
    /// </remarks>
    public bool CanUnload => Material is not null;
}
