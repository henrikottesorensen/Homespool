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
public sealed record PrinterShortcut(
    Printer Printer,
    string Name,
    bool Connected,
    PrinterStatus? LiveStatus,
    PrinterFormFactor FormFactor,
    int RecentJobs);
