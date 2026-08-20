namespace Homespool.Model;

/// <summary>
/// Why a printer's queue is held behind its next file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Replaced a sentence, and that is the point.</b> <c>PrintFileOnPrinter.BlockedReason</c> stored
/// finished English prose, which made the column do two jobs badly: it was the only record of *what
/// happened*, and it was the text shown to whoever was reading the page. The second job meant it
/// could not be translated; the first meant the free-space check had to identify its own holds by
/// prefix-matching its own opening words, because nothing said who wrote the row.
/// </para>
/// <para>
/// <b>The prefix match was the sharper problem.</b> Translating the sentence would have left a
/// Danish string in the column and an English prefix in the comparison, so the hold would never have
/// lifted — a printer that stays stopped after somebody frees the space. A localisation change
/// causing a queue to wedge is the kind of coupling worth removing rather than working around, and
/// <c>QueueAdvancer</c>'s own comment had already asked for it: <i>"If a third writer appears, add
/// the column rather than a third prefix."</i>
/// </para>
/// <para>
/// <b>Stored as text, following <c>PrintJob.State</c>.</b> One row per file per printer, and a
/// non-null reason exists only when something is wrong — so every row of this column that is ever
/// read is read by somebody working out why a printer stopped. Integers save two bytes on rows
/// nobody reads and cost legibility on the only rows that matter. Text is also immune to the enum
/// being reordered, which earns more here than elsewhere: unlike <c>Events</c> or
/// <c>PrinterStatus</c>, this vocabulary mirrors no firmware, so nothing outside this repository
/// pins its order.
/// </para>
/// </remarks>
public enum PrintHoldReason
{
    /// <summary>Not a reason. Present so a default-valued row is not silently a real hold.</summary>
    Undefined = 0,

    /// <summary>The printer does not have room for the file.</summary>
    /// <remarks>
    /// Clears by itself: the loop re-checks, so somebody who deletes something at the panel sees the
    /// queue resume without pressing anything here.
    /// </remarks>
    InsufficientSpace,

    /// <summary>
    /// The printer already holds a different file under this name, and said how big it is.
    /// </summary>
    /// <remarks>
    /// Held rather than failed, because the queue entry is still wanted - see
    /// <c>PrintFileOnPrinter</c>'s remarks on holding like a spooler.
    /// </remarks>
    FileExistsDifferentSize,

    /// <summary>
    /// The printer already holds a file under this name and would not say how big it is.
    /// </summary>
    /// <remarks>
    /// A separate member rather than a null size on the one above, because the two say different
    /// things to a reader: one is demonstrably not our file, the other cannot be confirmed either
    /// way. Collapsing them would make the page claim a certainty the printer did not give.
    /// </remarks>
    FileExistsUnknownSize,

    /// <summary>
    /// The print uses abrasive filament and the printer reports no hardened nozzle for it to pass
    /// through.
    /// </summary>
    /// <remarks>
    /// <b>Clears by itself, like the space hold and unlike the name ones.</b> The comparison is
    /// recomputed on every pass from what the printer last reported, so fitting a hardened nozzle
    /// and letting the printer say so lifts this with nobody pressing anything here.
    /// </remarks>
    AbrasiveFilamentNeedsHardenedNozzle,

    /// <summary>
    /// The file was sliced for a printer model this one cannot print for.
    /// </summary>
    /// <remarks>
    /// Wear rather than a wasted print, which is why it holds rather than warning: a file sliced for
    /// a CoreXY carries accelerations a bed slinger should not be asked to sustain. Directional -
    /// the older machine's file on the newer one is fine and never reaches here.
    /// </remarks>
    IncompatiblePrinterModel,
}
