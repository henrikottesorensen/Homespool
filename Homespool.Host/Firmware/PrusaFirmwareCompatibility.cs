using System;
using System.Collections.Generic;
using System.Globalization;

namespace Homespool.Host.Firmware;

/// <summary>Which printers a Prusa firmware image is built for.</summary>
/// <remarks>
/// <para>
/// <b>An image names the build it is, not every printer it runs on.</b> Its header carries one
/// <c>printer_type</c> triple, and some builds serve several models, each of which reports its own
/// triple once running: the MK4 build serves the MK4, MK4S, MK3.9 and MK3.9S, the MK3.5 build the
/// MK3.5 and MK3.5S, the XL build the XL and XL+. Those three are firmware's
/// <c>extended_printer_type_model</c> (<c>include/common/extended_printer_type.hpp</c>). The Core One
/// build also serves the Core One "Oak", which reports <c>7.2.0</c> but, in firmware's own words,
/// shares build-level printer version 7.1.0 with Core One to avoid bootloader changes
/// (<c>src/common/printer_model.cpp</c>). Every other build serves exactly its own triple.
/// </para>
/// <para>
/// <b>A filter for what to offer, not the last word.</b> The printer's bootloader decides whether an
/// image matches its hardware, and refuses one that does not - but at the printer, where somebody has
/// to go and see the refusal. An image missing from this table's answer is one nobody is offered;
/// one wrongly in it would be refused there.
/// </para>
/// </remarks>
public static class PrusaFirmwareCompatibility
{
    private static readonly Dictionary<string, string[]> Served = new(StringComparer.Ordinal)
    {
        ["1.4.0"] = ["1.4.0", "1.4.1", "1.3.9", "1.3.10"],
        ["1.3.5"] = ["1.3.5", "1.3.6"],
        ["3.1.0"] = ["3.1.0", "3.1.1"],
        ["7.1.0"] = ["7.1.0", "7.2.0"],
    };

    /// <summary>The <c>printer_type</c> triple the image was built as - <c>7.1.0</c> for a Core One.</summary>
    public static string BuildOf(PrusaFirmwareHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);

        return string.Create(CultureInfo.InvariantCulture,
                             $"{header.PrinterType}.{header.PrinterVersion}.{header.PrinterSubversion}");
    }

    /// <summary>
    /// Whether the image runs on a printer reporting <paramref name="printerType"/>, the triple a
    /// printer's <c>INFO</c> carries and <c>Printer.Model</c> holds. False for a printer that has not
    /// said.
    /// </summary>
    public static bool Fits(PrusaFirmwareHeader header, string? printerType)
    {
        if (printerType is null)
        {
            return false;
        }

        string build = BuildOf(header);

        return Served.TryGetValue(build, out string[]? models) ?
            Array.IndexOf(models, printerType) >= 0 :
            string.Equals(build, printerType, StringComparison.Ordinal);
    }
}
