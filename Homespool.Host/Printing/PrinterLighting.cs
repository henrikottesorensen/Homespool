using Homespool.Model;

namespace Homespool.Host.Printing;

/// <summary>
/// Which printers have lighting this application can set, and whether they say how bright it is.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two ways to know, and either is enough.</b> A printer that has reported a brightness has the
/// lighting, whatever its model; a model that firmware builds with the strips has it before it has
/// reported anything. The second matters for the XL, which never reports one.
/// </para>
/// <para>
/// <b>The model list is firmware's <c>PRINTERS_WITH_SIDE_LEDS</c></b> (<c>ProjectOptions.cmake</c> at
/// <c>v6.10.1</c>): the XL and every CORE One. The XL+ is an XL build, and the INDX machines are CORE
/// One builds. Anything else is refused here rather than sent, because a build without the strips
/// answers the command <i>"Missing or broken parameters"</i>, which would tell nobody what happened.
/// </para>
/// </remarks>
public static class PrinterLighting
{
    /// <summary>The brightest setting, in percent - the strip at full.</summary>
    public const int MaxIntensity = 100;

    /// <summary>Whether this printer has lighting that can be set.</summary>
    /// <param name="printerType">The printer's model as it reported it - <c>Printer.Model</c>, a triple such as <c>7.1.0</c>.</param>
    /// <param name="reportedIntensity">The brightness it last reported, or null when it never has.</param>
    public static bool Has(string? printerType, int? reportedIntensity)
    {
        return reportedIntensity is not null || IsBuiltWith(PrinterModelCompatibility.GroupForPrinterType(printerType));
    }

    /// <summary>
    /// Whether this printer's model has lighting and never reports how bright it is - an XL.
    /// </summary>
    /// <remarks>
    /// Firmware renders the brightness only inside the CORE One's <c>chamber</c> telemetry block
    /// (<c>render.cpp</c>), so the XL takes the setting and says nothing about it afterwards. A
    /// brightness stored for one is the last one set from here, and a change made at its panel is
    /// invisible.
    /// </remarks>
    public static bool Unreported(string? printerType)
    {
        return PrinterModelCompatibility.GroupForPrinterType(printerType) is PrinterModelGroup.Xl or PrinterModelGroup.Xlp;
    }

    /// <summary>
    /// What the printer will report after being set to <paramref name="intensity"/>, which is not
    /// always that number.
    /// </summary>
    /// <remarks>
    /// Firmware keeps the setting as a byte, <c>percent * 255 / 100</c>, and reports it back as
    /// <c>byte * 100 / 255</c>, both in integers (<c>planner.cpp</c>, <c>marlin_printer.cpp</c>). The
    /// round trip loses a step at some values: 33 is stored as 84 and read back as 32.
    /// </remarks>
    /// <param name="intensity">A brightness in percent, 0 to <see cref="MaxIntensity"/>.</param>
    public static int ReadBack(int intensity)
    {
        return intensity * 255 / 100 * 100 / 255;
    }

    private static bool IsBuiltWith(PrinterModelGroup group)
    {
        return group is PrinterModelGroup.Xl or
                        PrinterModelGroup.Xlp or
                        PrinterModelGroup.CoreOne or
                        PrinterModelGroup.CoreOneL or
                        PrinterModelGroup.CoreOneIndx or
                        PrinterModelGroup.CoreOneLIndx;
    }
}
