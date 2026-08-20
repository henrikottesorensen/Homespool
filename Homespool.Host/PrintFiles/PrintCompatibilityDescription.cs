using System;
using System.Collections.Generic;

using Homespool.Host.Localisation;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.PrintFiles;

/// <summary>
/// Names the sentence for a <see cref="PrintCompatibilityFinding"/>, for whoever just queued a file.
/// </summary>
/// <remarks>
/// <para>
/// <b>A key rather than the words</b>, so the page decides the language - the same trade
/// <c>QueueWaitDescription</c> and the hold reasons make. The numbers travel as numbers, not as
/// preformatted text, so a Danish reader gets <c>0,4</c> and an English one <c>0.4</c>
/// (<c>notes/localisation.md</c>).
/// </para>
/// <para>
/// <b>The two holding findings say what will happen, and the warnings do not.</b> "It is queued but
/// will not start" is the whole of what separates them for a reader, and a warning that sounds like
/// a refusal is how people learn to ignore both.
/// </para>
/// </remarks>
public static class PrintCompatibilityDescription
{
    /// <summary>The sentence a finding wants, filled from the two rows it came from.</summary>
    public static MessageKey For(PrintCompatibilityFinding finding,
                                 PrintFile file,
                                 Printer printer,
                                 IReadOnlyList<PrinterTool> tools)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(printer);
        ArgumentNullException.ThrowIfNull(tools);

        return finding switch
        {
            PrintCompatibilityFinding.AbrasiveFilamentNeedsHardenedNozzle =>
                MessageKey.For("Queue_WarnAbrasiveNeedsHardened", file.Name),

            PrintCompatibilityFinding.AbrasiveFilamentMayUseASoftNozzle =>
                MessageKey.For("Queue_WarnAbrasiveMayUseSoft", file.Name),

            PrintCompatibilityFinding.IncompatiblePrinterModel =>
                MessageKey.For("Queue_WarnIncompatibleModel",
                               file.Name,
                               file.PrinterModel ?? string.Empty,
                               printer.Model ?? string.Empty),

            PrintCompatibilityFinding.NozzleDiameterMismatch =>
                MessageKey.For("Queue_WarnNozzleDiameter",
                               file.Name,
                               file.NozzleDiameter ?? 0f,
                               PrintFileCompatibility.FittedNozzleDiameter(printer, tools) ?? 0f),

            PrintCompatibilityFinding.HighFlowNozzleRequired =>
                MessageKey.For("Queue_WarnHighFlow", file.Name),

            // Undefined is not a finding and nothing produces it - see PrintCompatibilityFinding.
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding, "No sentence for this finding."),
        };
    }
}
