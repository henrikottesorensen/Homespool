using Homespool.Model.Entities;

namespace Homespool.Host.Pages;

/// <summary>
/// What to call a printer on screen when it may not have been named.
/// </summary>
/// <remarks>
/// <b>Shared, for the same reason <see cref="Printers.PrinterStatusBadge"/> is.</b> Two pages naming
/// the same printer differently is a bug a reader reports as "it is called something else on the
/// front page", and the fallback chain is exactly the kind of thing that gets a third link added in
/// one copy only.
/// </remarks>
public static class PrinterDisplayName
{
    /// <summary>
    /// The printer's name, the model it reported, or its uuid - the first of those it has.
    /// </summary>
    /// <remarks>
    /// <b>The uuid is a last resort and looks like one</b>, which is deliberate: a wall of hex is a
    /// legible prompt to go and name the thing, where "Printer 4" would read as a name somebody chose.
    /// </remarks>
    public static string For(Printer printer)
    {
        System.ArgumentNullException.ThrowIfNull(printer);

        if (!string.IsNullOrWhiteSpace(printer.Name))
        {
            return printer.Name;
        }

        return !string.IsNullOrWhiteSpace(printer.Model) ? printer.Model : printer.Uuid.ToString();
    }
}
