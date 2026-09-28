using Homespool.Model;
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
    /// <para>
    /// <b>The uuid is a last resort and looks like one</b>, which is deliberate: a wall of hex is a
    /// legible prompt to go and name the thing, where "Printer 4" would read as a name somebody chose.
    /// </para>
    /// <para>
    /// <b>The model is resolved before it is shown.</b> What the printer reported is
    /// <c>printer_type</c>, a version triple, so shown raw an unnamed printer would be called
    /// <c>1.3.5</c> - which is not a worse name than the uuid so much as a name that looks like it
    /// means something and does not.
    /// </para>
    /// </remarks>
    public static string For(Printer printer)
    {
        System.ArgumentNullException.ThrowIfNull(printer);

        if (!string.IsNullOrWhiteSpace(printer.Name))
        {
            return printer.Name;
        }

        string? model = PrinterModelDesignation.Of(printer.Model);

        return !string.IsNullOrWhiteSpace(model) ? model : printer.Uuid.ToString();
    }
}
