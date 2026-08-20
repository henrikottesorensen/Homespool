using System.Collections.Generic;

using Homespool.Host.Localisation;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Queue;

/// <summary>
/// What came of adding a file to a queue: the entry, and anything worth telling whoever added it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The entry is always created, whatever the findings say.</b> Warning at the queue attempt and
/// holding before the job starts are two different jobs (Henrik, 2026-08-19): this is the moment a
/// person is standing there and can be told, and the hold is what stops the print later if nothing
/// has changed by then. Refusing here instead would lose the self-clearing property - fit the right
/// nozzle and the held entry simply runs.
/// </para>
/// <para>
/// <b>Findings rather than sentences</b>, because the caller decides how to say them: a page has a
/// reader and a culture, and the compat endpoint a slicer posts to has neither. See
/// <c>PrintCompatibilityDescription</c> for the wording.
/// </para>
/// </remarks>
/// <param name="Queued">The entry, now sitting at the end of the printer's queue.</param>
/// <param name="Findings">
/// How the file and the printer disagree, most serious first. Empty means nothing is known to be
/// wrong - which includes the printer never having said what hardware it has.
/// </param>
/// <param name="Warnings">
/// The same findings as sentences to be said, in the same order.
/// <para>
/// <b>Built here rather than by the page</b>, because the two rows the wording quotes - the file's
/// nozzle diameter against the fitted one - are already loaded to reach the findings at all. A page
/// composing them would re-read both, and a second reading is a second chance to quote a different
/// number than the comparison used.
/// </para>
/// </param>
public sealed record EnqueueOutcome(QueuedPrint Queued,
                                    IReadOnlyList<PrintCompatibilityFinding> Findings,
                                    IReadOnlyList<MessageKey> Warnings)
{
    /// <summary>The most serious thing found, or null when nothing was.</summary>
    public PrintCompatibilitySeverity? Severity => PrintFiles.PrintFileCompatibility.WorstOf(Findings);
}
