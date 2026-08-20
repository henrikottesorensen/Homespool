using System;
using System.Collections.Generic;
using System.Linq;

using Homespool.Host.PrintFiles.GCode;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.PrintFiles;

/// <summary>
/// Compares what a print file was sliced for against the printer it is aimed at.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure, and separate from anything that acts on it</b>, for the reason <c>QueueRules</c> is:
/// this is the gate standing between a queue and a nozzle being worn open, and a decision function
/// with no I/O can be tested over every combination of what the two sides did and did not report.
/// </para>
/// <para>
/// <b>Silence is the answer whenever a side did not say.</b> Every rule below needs both halves, and
/// a missing half yields no finding rather than a cautious one - a file no slicer annotated, a
/// printer that has not sent <c>INFO</c> yet, a model neither table knows. The alternative is a
/// check that cries wolf on ordinary uploads, which is how people learn to click through the one
/// that mattered.
/// </para>
/// <para>
/// <b>The printer checks every one of these itself, and that is not an argument against doing it
/// here.</b> Firmware's <c>gcode_compatibility.cpp</c> covers model, nozzle diameter, hardened and
/// high-flow, each defaulting to <c>HWCheckSeverity::Warning</c> - a prompt, at the machine, that a
/// person can click through or switch off. This runs before the bytes are sent and before anybody
/// has to be standing there, which for a queue is the difference between fixing a file and finding
/// the printer stopped an hour later. On the two findings that cost hardware it is also firmer than
/// firmware's default, which is a deliberate disagreement rather than an oversight.
/// </para>
/// <para>
/// <b>It does not check the material against what is loaded</b>, and that is a decision rather than
/// an omission. Unlike the four below, a wrong material is caught by the printer <i>and</i> cannot
/// be answered remotely anyway: the prompt asks for a different spool, and somebody has to go and
/// change it. Saying it twice buys nothing.
/// </para>
/// <para>
/// <b>It never second-guesses the printer's report</b> (Henrik: <i>"we gotta trust what the printer
/// is reporting"</i>). A fitted nozzle's diameter and whether it is hardened are settings somebody
/// maintains, not measurements, so they can be stale - and there is nothing better to compare
/// against, which makes a check that tries to out-guess them worse than none.
/// </para>
/// </remarks>
public static class PrintFileCompatibility
{
    /// <summary>What each finding costs, and therefore whether it holds the queue.</summary>
    public static PrintCompatibilitySeverity SeverityOf(PrintCompatibilityFinding finding)
    {
        return finding switch
        {
            PrintCompatibilityFinding.AbrasiveFilamentNeedsHardenedNozzle => PrintCompatibilitySeverity.Hold,
            PrintCompatibilityFinding.IncompatiblePrinterModel => PrintCompatibilitySeverity.Hold,
            PrintCompatibilityFinding.AbrasiveFilamentMayUseASoftNozzle => PrintCompatibilitySeverity.Warn,
            PrintCompatibilityFinding.NozzleDiameterMismatch => PrintCompatibilitySeverity.Warn,
            PrintCompatibilityFinding.HighFlowNozzleRequired => PrintCompatibilitySeverity.Warn,
            _ => PrintCompatibilitySeverity.Undefined,
        };
    }

    /// <summary>
    /// Everything wrong with sending <paramref name="file"/> to <paramref name="printer"/>, most
    /// serious first. Empty means nothing is known to be wrong - which includes knowing nothing.
    /// </summary>
    /// <param name="file">The index row, carrying what the file said about itself when it landed.</param>
    /// <param name="printer">The printer, carrying what it last reported about itself.</param>
    /// <param name="tools">
    /// The printer's per-tool hardware. Empty when it has never reported a <c>tools</c> block, which
    /// silences the two rules that depend on one.
    /// </param>
    public static IReadOnlyList<PrintCompatibilityFinding> Evaluate(PrintFile file,
                                                                    Printer printer,
                                                                    IReadOnlyList<PrinterTool> tools)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(printer);
        ArgumentNullException.ThrowIfNull(tools);

        List<PrintCompatibilityFinding> findings = [];

        if (PrinterModelCompatibility.CanPrint(printer.Model, file.PrinterModel) == false)
        {
            findings.Add(PrintCompatibilityFinding.IncompatiblePrinterModel);
        }

        if (file.RequiresHardenedNozzle == true && AbrasiveFindingFor(tools) is { } abrasive)
        {
            findings.Add(abrasive);
        }

        if (file.NozzleDiameter is { } wanted
            && FittedNozzleDiameter(printer, tools) is { } fitted
            && Math.Abs(wanted - fitted) > GCodeMetadata.NozzleDiameterTolerance)
        {
            findings.Add(PrintCompatibilityFinding.NozzleDiameterMismatch);
        }

        if (file.RequiresHighFlowNozzle == true && tools.Count > 0 && tools.All(tool => !tool.HighFlow))
        {
            findings.Add(PrintCompatibilityFinding.HighFlowNozzleRequired);
        }

        return findings;
    }

    /// <summary>The most serious severity among <paramref name="findings"/>, or null if there are none.</summary>
    public static PrintCompatibilitySeverity? WorstOf(IReadOnlyList<PrintCompatibilityFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        return findings.Count == 0 ? null : findings.Max(SeverityOf);
    }

    /// <summary>
    /// What an abrasive print amounts to on this printer's nozzles, or null if nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three answers, and the middle one is the interesting case.</b> Every tool hardened is
    /// fine; none hardened is definitely wrong and holds. <b>Some of each warns</b>, because which
    /// tool the abrasive filament goes through is settled by the file's tool mapping, which firmware
    /// resolves at print time and this cannot see - so holding would stop legitimate prints, and
    /// silence would say nothing about the only finding here that costs hardware.
    /// </para>
    /// <para>
    /// <b>Why the uncertain case is voiced here and swallowed by the other rules.</b> A nozzle
    /// diameter or a high-flow hotend this cannot pin down costs a bad print, and a maybe about a
    /// bad print is noise. A maybe about permanent damage is worth a person's attention, and it is
    /// the asymmetry in the cost that earns it - not a difference in how well the two are known.
    /// </para>
    /// <para>
    /// A printer that has reported no tools at all says nothing either - there is no report to trust
    /// yet, and this rule refuses to invent one.
    /// </para>
    /// </remarks>
    private static PrintCompatibilityFinding? AbrasiveFindingFor(IReadOnlyList<PrinterTool> tools)
    {
        if (tools.Count == 0 || tools.All(tool => tool.Hardened))
        {
            return null;
        }

        return tools.Any(tool => tool.Hardened) ?
            PrintCompatibilityFinding.AbrasiveFilamentMayUseASoftNozzle :
            PrintCompatibilityFinding.AbrasiveFilamentNeedsHardenedNozzle;
    }

    /// <summary>
    /// The nozzle diameter to compare against, or null when the printer has not reported one.
    /// </summary>
    /// <remarks>
    /// Public because the sentence shown to a person has to quote the same figure the comparison
    /// used - two ways of picking it would disagree on a toolchanger and nowhere else, which is the
    /// worst possible place for them to disagree.
    /// </remarks>
    /// <remarks>
    /// <b>A single tool's own figure beats the top-level one</b>, which is the same number for a
    /// single-tool machine and the only meaningful one when it is not. <b>A toolchanger whose tools
    /// differ is left alone</b>: the file records one diameter only when its own extruders agreed,
    /// so comparing that against whichever of several fitted nozzles came first would be a claim
    /// about the wrong tool.
    /// </remarks>
    public static float? FittedNozzleDiameter(Printer printer, IReadOnlyList<PrinterTool> tools)
    {
        if (tools.Count == 0)
        {
            return printer.NozzleDiameter;
        }

        float? first = tools[0].NozzleDiameter;

        return tools.All(tool => tool.NozzleDiameter is { } diameter
                                 && first is { } head
                                 && Math.Abs(diameter - head) < GCodeMetadata.NozzleDiameterTolerance) ?
            first :
            null;
    }
}
