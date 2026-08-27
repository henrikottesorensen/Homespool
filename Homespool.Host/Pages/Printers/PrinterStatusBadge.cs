using Homespool.Model;

namespace Homespool.Host.Pages.Printers;

/// <summary>
/// How a printer's status is dressed on a page - the badge's colour, and the shape that says whether
/// the printer is there at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared by the listing and the detail page, so the two cannot drift.</b> Both answer "what is
/// this printer doing" in the same vocabulary, and a second copy of the mapping would be free to
/// disagree one page at a time.
/// </para>
/// <para>
/// <b>Nothing here is localised, and that is the boundary rather than an omission.</b> These return
/// CSS class names - values Bootstrap parses, not values a person reads - so translating one would
/// produce a class that styles nothing. The words beside them come from
/// <see cref="Localisation.PrinterStatusText"/>: same enum, same corner of the page, opposite sides
/// of the same line.
/// </para>
/// </remarks>
public static class PrinterStatusBadge
{
    /// <summary>
    /// The badge classes for a status - semantic, so what needs a person reads at a glance.
    /// </summary>
    /// <remarks>
    /// Three tiers and a default: <b>danger</b> for the two that need somebody now, <b>success</b>
    /// for a printer doing what it was asked, and <b>secondary</b> for every resting state, so a rack
    /// of idle printers stays quiet instead of glowing green.
    /// </remarks>
    public static string For(PrinterStatus? status)
    {
        return status switch
        {
            PrinterStatus.Error or PrinterStatus.Attention => "text-bg-danger",
            PrinterStatus.Paused => "text-bg-warning",
            PrinterStatus.Printing or PrinterStatus.Ready => "text-bg-success",
            _ => "text-bg-secondary",
        };
    }

    /// <summary>
    /// The badge classes for a printer that is not connected.
    /// </summary>
    /// <remarks>
    /// <b>Hollow, and the shape is the whole point.</b> Absence is not a severity - a printer
    /// switched off overnight is offline and perfectly well - so it gets a form of its own rather
    /// than a hue that would over-claim on the common case.
    /// </remarks>
    public const string Absent = "border border-secondary text-body-secondary";
}
