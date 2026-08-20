using System.Collections.Generic;
using System.Linq;

namespace Homespool.Host.PrintFiles.GCode;

/// <summary>
/// What the slicer said about the hardware it sliced a print file for: the five values a printer
/// needs to decide whether it can print the thing at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every list is per-extruder, exactly as the slicer wrote it</b>, and no list is folded into a
/// single answer here. How to fold them is a question about the <i>printer</i> - an MMU puts five
/// filaments through one nozzle, so any abrasive one of them wears that nozzle, while a toolchanger
/// pairs extruder <i>n</i> with tool <i>n</i>. This type is the file's side of the comparison and
/// deliberately holds no opinion about the machine.
/// </para>
/// <para>
/// <b>Empty means the file did not say, which is an ordinary answer.</b> Output from a slicer that
/// is not PrusaSlicer carries none of these keys, and that must read as <i>cannot tell</i> rather
/// than as a mismatch.
/// </para>
/// </remarks>
/// <param name="PrinterModel">
/// The slicer's model designation, e.g. <c>COREONE</c>, <c>MK3.5</c>, <c>MK4IS</c>. <b>Not the same
/// vocabulary the printer reports</b> - firmware says <c>MK4</c> where the slicer says <c>MK4IS</c>,
/// so the two are compared through a mapping rather than as strings.
/// </param>
/// <param name="NozzleDiameters">Millimetres, one per extruder.</param>
/// <param name="FilamentTypes">Material names as the slicer spells them, e.g. <c>PLA</c>, <c>PCTG</c>.</param>
/// <param name="FilamentAbrasive">
/// Whether each filament wears a nozzle - carbon-fibre and metal fills. This is the one that costs
/// hardware rather than a print.
/// </param>
/// <param name="NozzleHighFlow">Whether each extruder's profile assumes a high-flow hotend.</param>
public sealed record GCodeMetadata(string? PrinterModel,
                                   IReadOnlyList<float> NozzleDiameters,
                                   IReadOnlyList<string> FilamentTypes,
                                   IReadOnlyList<bool> FilamentAbrasive,
                                   IReadOnlyList<bool> NozzleHighFlow)
{
    /// <summary>
    /// How close two nozzle diameters have to be to count as the same one, in millimetres.
    /// </summary>
    /// <remarks>
    /// <b>Firmware's own figure</b> - <c>gcode_compatibility.cpp</c> fails its nozzle check on
    /// <c>abs(file - fitted) &gt; 0.001f</c>. Taken rather than chosen, so that a file this accepts
    /// is a file the printer will accept for the same reason, and matching to the third decimal is
    /// well inside the two decimals anybody actually writes.
    /// </remarks>
    public const float NozzleDiameterTolerance = 0.001f;

    /// <summary>A file that said nothing at all.</summary>
    public static GCodeMetadata Empty { get; } = new(null, [], [], [], []);

    /// <summary>
    /// Whether the file carried none of the five values, so nothing can be compared against it.
    /// </summary>
    public bool SaysNothing => PrinterModel is null
                               && NozzleDiameters.Count == 0
                               && FilamentTypes.Count == 0
                               && FilamentAbrasive.Count == 0
                               && NozzleHighFlow.Count == 0;

    /// <summary>
    /// Whether any filament in this print wears a nozzle. Null when the file did not say.
    /// </summary>
    /// <remarks>
    /// <b>Any, not each</b>, because this is the question a single-nozzle printer asks: an MMU
    /// pushes every one of its filaments through the one hotend, so one abrasive spool among five
    /// wears the same nozzle as five would.
    /// </remarks>
    public bool? AnyFilamentAbrasive => FilamentAbrasive.Count == 0 ? null : FilamentAbrasive.Any(abrasive => abrasive);

    /// <summary>
    /// Whether any extruder was sliced for a high-flow hotend. Null when the file did not say.
    /// </summary>
    public bool? AnyNozzleHighFlow => NozzleHighFlow.Count == 0 ? null : NozzleHighFlow.Any(highFlow => highFlow);
}
