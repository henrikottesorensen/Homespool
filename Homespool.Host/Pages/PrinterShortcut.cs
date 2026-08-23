using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages;

/// <summary>
/// One printer's tile on the front page: a drawing, a name, and what it is doing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Flattened deliberately.</b> The tile renders inside a polled fragment, and the rule
/// <c>notes/printer-page.md</c> §6e paid for is that a polled fragment may only render state its own
/// handler loads. Handing the view a record it can read end to end - rather than a printer it must
/// walk relationships from - is what makes that rule easy to keep.
/// </para>
/// <para>
/// <b><see cref="LiveStatus"/> is null until the printer has ever reported</b>, like the listing's
/// row beside it, and is not <c>Printer.Status</c> - that field is written once at creation and never
/// updated.
/// </para>
/// </remarks>
/// <param name="Printer">The printer the tile links to.</param>
/// <param name="Name">What to call it, from <see cref="PrinterDisplayName"/>.</param>
/// <param name="Connected">Whether it is reachable right now, which decides the badge.</param>
/// <param name="LiveStatus">What it says it is doing, or null until it has ever reported.</param>
/// <param name="FormFactor">Which drawing it gets.</param>
/// <param name="RecentJobs">
/// How many jobs the reader queued on it inside the window. Not rendered - it is what the tiles were
/// ordered by, carried along so the ordering can be explained without running the query again.
/// </param>
/// <param name="Progress">Percent complete, or null when nothing is printing.</param>
/// <param name="TimeRemaining">
/// Seconds left on the running print, as firmware reports it. Null is common and legitimate even
/// mid-print - an estimate is not always available - so the tile shows the bar without the words
/// rather than inventing a figure.
/// </param>
/// <param name="Material">
/// The filament the printer says is loaded. Shown as it arrives, unmapped: firmware's own vocabulary
/// is the vocabulary a person reading it already uses, and a translation table would be a second
/// place for "PLA" to be spelled.
/// </param>
/// <param name="QueuedCount">How many files are waiting. Zero renders nothing.</param>
public sealed record PrinterShortcut(
    Printer Printer,
    string Name,
    bool Connected,
    PrinterStatus? LiveStatus,
    PrinterFormFactor FormFactor,
    int RecentJobs,
    int? Progress,
    int? TimeRemaining,
    string? Material,
    int QueuedCount);
