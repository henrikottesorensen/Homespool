namespace Homespool.Model;

/// <summary>
/// Turns what a printer reports about itself into the designation everything else is written in.
/// </summary>
/// <remarks>
/// <para>
/// <b>A printer does not report <c>MK3.5</c>; it reports <c>1.3.5</c>.</b> <c>INFO</c>'s
/// <c>printer_type</c> is a version triple, and that is the string stored against the printer - so
/// anything comparing it against a designation, whether a compatibility table, a preheat rule or a
/// sentence shown to a person, needs this first. The failure without it is silent in both directions:
/// a table lookup finds an unknown model and makes no claim, and a sentence prints the triple as
/// though it were a name.
/// </para>
/// <para>
/// <b>Two answers, because matching and showing want different strings.</b> <see cref="Of"/> is
/// firmware's id - <c>XLP</c> - which is what the compatibility table and the slicer's vocabulary are
/// keyed on. <see cref="ForDisplay"/> is what the printer calls itself on its own screen - <c>XL+</c>.
/// They differ for one model today, which is exactly how a lookup keyed on the displayed name would
/// quietly miss.
/// </para>
/// <para>
/// <b>The raw value is returned when it resolves to nothing</b>, which covers two cases at once: a
/// printer newer than <see cref="PrinterModelNames"/>, whose triple is the most honest thing there is
/// to say about it, and a designation that reached this by some route other than the wire.
/// </para>
/// </remarks>
public static class PrinterModelDesignation
{
    /// <summary>
    /// The designation for a reported <c>printer_type</c> - <c>MK3.5</c> for <c>1.3.5</c> - or the
    /// value itself where this is not a triple the table knows.
    /// </summary>
    public static string? Of(string? printerType)
    {
        return PrinterModelNames.ForPrinterType(printerType) ?? printerType;
    }

    /// <summary>
    /// The name to show a person for a reported <c>printer_type</c> - <c>XL+</c> for <c>3.1.1</c>,
    /// <c>MK3.5</c> for <c>1.3.5</c> - or the value itself where this is not a triple the table knows.
    /// Never match on it; see <see cref="Of"/>.
    /// </summary>
    public static string? ForDisplay(string? printerType)
    {
        return PrinterModelNames.DisplayNameForPrinterType(printerType) ?? printerType;
    }
}
