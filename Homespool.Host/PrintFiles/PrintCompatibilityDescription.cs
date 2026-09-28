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
/// preformatted text, so a Danish reader gets <c>0,4</c> and an English one <c>0.4</c>.
/// </para>
/// <para>
/// <b>The holding finding says what will happen, and the warnings do not.</b> "It is queued but will
/// not start" is the whole of what separates them for a reader, and a warning that sounds like a
/// refusal is how people learn to ignore both.
/// </para>
/// <para>
/// <b>Only findings that still queue have a sentence here.</b> A file sliced for a different machine
/// is refused before an entry exists, so it has nothing to be warned about; its words belong to the
/// refusal.
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

            // Refused before anything is queued, so there is never a queued entry to warn about - the
            // refusal carries its own sentence, on IncompatiblePrinterModelException. A warning here
            // would have to say "it has been queued", which is never true of this finding.
            PrintCompatibilityFinding.IncompatiblePrinterModel =>
                throw new ArgumentOutOfRangeException(nameof(finding), finding, "This finding refuses the queue rather than warning."),

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
