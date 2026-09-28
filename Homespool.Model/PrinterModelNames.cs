using System.Collections.Generic;

namespace Homespool.Model;

/// <summary>
/// Maps the version triple a printer reports as <c>printer_type</c> ("1.3.5") to the name Prusa
/// call it ("MK3.5"), and to the name the printer shows on its own screen where that differs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Generated - do not edit.</b> Run <c>tools/printer-models/generate.py</c> against a
/// Prusa-Firmware-Buddy checkout to refresh it. Source of truth is that repository's
/// <c>include/common/printer_model_data.hpp</c>; this file was generated from
/// <c>1ce23f33e</c> on 2026-09-28 and holds 18 models.
/// </para>
/// <para>
/// The firmware checkout is a <b>developer-time</b> dependency, never a build-time one - the
/// generated file is committed and compiles like any other source, so a clean build needs nothing
/// from Prusa's tree. Same arrangement as <c>render-fixtures.json</c>.
/// </para>
/// <para>
/// Staleness is additive and benign: a version triple cannot change meaning without breaking every
/// deployed printer, so an out-of-date table lacks new models rather than misnaming old ones. An
/// unknown triple yields no name at all, which is the honest-nulls rule the API already follows.
/// </para>
/// </remarks>
public static class PrinterModelNames
{
    private static readonly Dictionary<string, string> Names = new()
    {
        ["1.3.0"] = "MK3",
        ["1.3.1"] = "MK3S",
        ["1.3.5"] = "MK3.5",
        ["1.3.6"] = "MK3.5S",
        ["1.3.9"] = "MK3.9",
        ["1.3.10"] = "MK3.9S",
        ["1.4.0"] = "MK4",
        ["1.4.1"] = "MK4S",
        ["2.1.0"] = "MINI",
        ["3.1.0"] = "XL",
        ["5.1.0"] = "XL",
        ["4.1.0"] = "iX",
        ["7.1.0"] = "COREONE",
        ["8.1.0"] = "COREONEL",
        ["7.10.0"] = "COREONEINDX",
        ["8.10.0"] = "COREONEL-INDX",
        ["7.2.0"] = "COREONEOAK",
        ["3.1.1"] = "XLP",
    };

    /// <summary>
    /// Firmware's <c>display_str_override</c>: what a printer calls itself on its own screen, for the
    /// few models where that differs from the id. Everything absent here displays as its id.
    /// </summary>
    private static readonly Dictionary<string, string> DisplayNames = new()
    {
        ["3.1.1"] = "XL+",
    };

    /// <summary>
    /// The model id for a <c>printer_type</c> triple - firmware's <c>id_str</c>, e.g. <c>XLP</c> -
    /// or <c>null</c> if this table has never heard of it: a printer newer than the firmware
    /// checkout this was generated from.
    /// </summary>
    /// <remarks>
    /// <b>The id is what other tables are keyed on</b>, the slicer's vocabulary included, so this is
    /// the one to match against. For what to show a person, use
    /// <see cref="DisplayNameForPrinterType"/>.
    /// </remarks>
    public static string? ForPrinterType(string? printerType)
    {
        return printerType is not null && Names.TryGetValue(printerType, out string? name) ? name : null;
    }

    /// <summary>
    /// What the printer shows as its own name - firmware's <c>display_str()</c>, e.g. <c>XL+</c> where
    /// the id is <c>XLP</c> - or <c>null</c> for a triple this table has never heard of.
    /// </summary>
    public static string? DisplayNameForPrinterType(string? printerType)
    {
        return printerType is not null && DisplayNames.TryGetValue(printerType, out string? display) ?
            display :
            ForPrinterType(printerType);
    }
}
