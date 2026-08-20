using System;
using System.Collections.Generic;

namespace Homespool.Model;

/// <summary>
/// Which printers may print a file sliced for which model - firmware's own compatibility table,
/// mirrored.
/// </summary>
/// <remarks>
/// <para>
/// <b>The relation is directional, and that is the whole value of it.</b> A printer accepts a file
/// sliced for itself or for an <i>older</i> model in its upgrade path, and never the other way
/// round: an MK3 file prints on an MK3.5, and a CORE One file must not be sent to one. The reason is
/// mechanical rather than cosmetic - a CoreXY's accelerations and speeds are not something a bed
/// slinger should be asked to sustain, whatever it would do if told to.
/// </para>
/// <para>
/// <b>Hand-maintained, unlike <see cref="PrinterModelNames"/> beside it</b>, and the split follows
/// firmware's own: the groups are data in <c>include/common/printer_model_data.hpp</c>, but the
/// upgrade path is a <c>switch</c> in <c>src/common/printer_model.cpp</c>'s
/// <c>gcode_compatibility_report_constexpr</c>. <b>Read at tag <c>v6.8.1</c></b>, which is ahead of
/// the <c>e96ce2b92</c> the wire-format citations pin - those describe a protocol that has not
/// moved, where this describes a catalogue that gains a machine every few months.
/// </para>
/// <para>
/// <b>Staleness is additive and benign</b>, which is what makes a hand-maintained table acceptable
/// for something that blocks: a model neither side knows yields
/// <see cref="PrinterModelGroup.Unknown"/> and therefore no claim, so a new machine loses the check
/// rather than gaining a wrong refusal. <b>Known missing as of <c>v6.8.1</c>: the Core One+ Gen 2
/// and the Core One L+</b>, which have been announced but carry no firmware entry - so a file
/// sliced for either is unrecognised, and a print of one on an older CORE One goes unchecked.
/// </para>
/// <para>
/// <b>Where this deliberately differs from firmware: an unrecognised model makes no claim, where
/// firmware fails the check</b> (<c>"Unknown gcode printer, sayonara!"</c>). Firmware is the last
/// gate and can afford to refuse; this one runs before the bytes are sent and would otherwise
/// block every MK2-era file and every printer newer than this table. The printer still refuses at
/// the end, so nothing is lost but the early warning.
/// </para>
/// <para>
/// <b>What a model comparison cannot reach, and no comparison here ever will: the belt pitch.</b>
/// The CORE One Gen 2 and L upgrades fit 1.5GT belts, and firmware carries that as
/// <c>belts_15gt_installed</c> - a stored hardware-configuration flag that, in its own words,
/// "influence[s] the X/Y steps/mm". <b>Firmware compensates for it, so the G-code is identical
/// either way</b>: there is nothing in a file to compare against, and the upgraded machine keeps
/// reporting <c>COREONE</c>, so the model is the same on both sides too. The failure mode is
/// physical belts disagreeing with the flag, which prints correct-looking G-code at the wrong size,
/// and <b>the flag never reaches the Connect wire</b> (no mention of belts anywhere under
/// <c>src/connect</c> at <c>v6.8.1</c>) - so Homespool cannot see it, let alone check it.
/// </para>
/// </remarks>
public static class PrinterModelCompatibility
{
    /// <summary>
    /// Suffixes the slicer adds that name firmware or an add-on rather than a different machine, so
    /// they are stripped before the lookup.
    /// </summary>
    /// <remarks>
    /// <b><c>IS</c> is Input Shaping - <c>MK4IS</c> is an MK4 with it switched on</b>, the same
    /// hardware, which is why firmware's table has no such entry to match. The MMU suffixes are the
    /// multi-material unit, which firmware does carry as separate ids mapping back to the base
    /// model. Longest first, because <c>MMU2S</c> would otherwise be left holding an <c>S</c>.
    /// </remarks>
    private static readonly string[] SlicerSuffixes = ["MMU2S", "MMU3", "MMU2", "MMU1", "MMU", "IS"];

    /// <summary>
    /// Model designation to compatibility group, from firmware's <c>printer_model_data.hpp</c>.
    /// </summary>
    /// <remarks>
    /// Keyed case-insensitively because two vocabularies meet here: the printer reports
    /// <c>iX</c> through <see cref="PrinterModelNames"/> while a slicer writes what its vendor
    /// bundle says.
    /// </remarks>
    private static readonly Dictionary<string, PrinterModelGroup> Groups =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["MK3"] = PrinterModelGroup.Mk3,
            ["MK3S"] = PrinterModelGroup.Mk3,
            ["MK3.5"] = PrinterModelGroup.Mk3_5,
            ["MK3.5S"] = PrinterModelGroup.Mk3_5,
            ["MK3.9"] = PrinterModelGroup.Mk4,
            ["MK3.9S"] = PrinterModelGroup.Mk4S,
            ["MK4"] = PrinterModelGroup.Mk4,
            ["MK4S"] = PrinterModelGroup.Mk4S,
            ["MINI"] = PrinterModelGroup.Mini,
            ["XL"] = PrinterModelGroup.Xl,
            ["XLP"] = PrinterModelGroup.Xlp,
            ["iX"] = PrinterModelGroup.Ix,
            ["COREONE"] = PrinterModelGroup.CoreOne,

            // Firmware puts the Oak in the plain CORE One group - a finish, not a machine.
            ["COREONEOAK"] = PrinterModelGroup.CoreOne,
            ["COREONEL"] = PrinterModelGroup.CoreOneL,
            ["COREONEINDX"] = PrinterModelGroup.CoreOneIndx,
            ["COREONEL-INDX"] = PrinterModelGroup.CoreOneLIndx,
        };

    /// <summary>
    /// The group a model designation belongs to, or <see cref="PrinterModelGroup.Unknown"/> if this
    /// table has never heard of it.
    /// </summary>
    public static PrinterModelGroup GroupFor(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return PrinterModelGroup.Unknown;
        }

        string current = model.Trim();

        // Stripped repeatedly rather than once, because they stack: MK4ISMMU3 is an MK4 with Input
        // Shaping and a multi-material unit, and peeling only the MMU off leaves MK4IS, which
        // firmware's table has never heard of either. Each pass shortens the string, so it ends.
        while (true)
        {
            if (Groups.TryGetValue(current, out PrinterModelGroup group))
            {
                return group;
            }

            string? stripped = StripSuffix(current) ?? WithoutToolCount(current);

            if (stripped is null)
            {
                return PrinterModelGroup.Unknown;
            }

            current = stripped;
        }
    }

    /// <summary>
    /// The name with its toolhead count removed, or null if it does not carry one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The slicer names a machine by how many tools it has and firmware does not</b>, so the
    /// vendor bundle ships <c>XL</c>, <c>XL2</c> and <c>XL5</c> - and <c>XL2IS</c>, <c>XL5IS</c> -
    /// where firmware's table holds one <c>XL</c>. Without this the only XL that got a model check
    /// was a single-toolhead one, since every other spelling fell through as unknown. Same for the
    /// INDX, whose <c>COREONE_INDX4T</c> and <c>COREONE_INDX8T</c> are firmware's one
    /// <c>COREONEINDX</c>.
    /// </para>
    /// <para>
    /// <b>Deliberately not a general "strip trailing digits" rule.</b> <c>MK2.5</c> and <c>MK3.9</c>
    /// carry digits that are part of the model, and a rule loose enough to eat a toolhead count
    /// would turn an MK2.5 into an MK2 - a machine that predates all of this and should stay
    /// unknown.
    /// </para>
    /// </remarks>
    private static string? WithoutToolCount(string model)
    {
        const string xl = "XL";
        const string indx = "COREONE_INDX";

        if (model.Length > xl.Length
            && model.StartsWith(xl, StringComparison.OrdinalIgnoreCase)
            && IsAllDigits(model.AsSpan(xl.Length)))
        {
            return xl;
        }

        if (model.Length > indx.Length + 1
            && model.StartsWith(indx, StringComparison.OrdinalIgnoreCase)
            && model.EndsWith("T", StringComparison.OrdinalIgnoreCase)
            && IsAllDigits(model.AsSpan(indx.Length, model.Length - indx.Length - 1)))
        {
            return "COREONEINDX";
        }

        return null;
    }

    private static bool IsAllDigits(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return !value.IsEmpty;
    }

    /// <summary>The name with one trailing slicer suffix removed, or null if it carries none.</summary>
    private static string? StripSuffix(string model)
    {
        foreach (string suffix in SlicerSuffixes)
        {
            if (model.Length > suffix.Length && model.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return model[..^suffix.Length];
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a printer of <paramref name="printerModel"/> may print a file sliced for
    /// <paramref name="fileModel"/>, or null when either model is one this table does not know.
    /// </summary>
    /// <param name="printerModel">As the printer reports it, e.g. <c>MK3.5</c>, <c>COREONE</c>.</param>
    /// <param name="fileModel">As the slicer wrote it, e.g. <c>MK4IS</c>, <c>COREONEMMU3</c>.</param>
    public static bool? CanPrint(string? printerModel, string? fileModel)
    {
        PrinterModelGroup printer = GroupFor(printerModel);
        PrinterModelGroup file = GroupFor(fileModel);

        if (printer == PrinterModelGroup.Unknown || file == PrinterModelGroup.Unknown)
        {
            return null;
        }

        // Walk the upgrade path from the printer downwards. It is a chain rather than a tree, so
        // this terminates in at most four steps - and a cycle cannot be expressed by UpgradesFrom.
        for (PrinterModelGroup group = printer; group != PrinterModelGroup.Unknown; group = UpgradesFrom(group))
        {
            if (group == file)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The older group whose files this one also accepts, or <see cref="PrinterModelGroup.Unknown"/>
    /// where there is none.
    /// </summary>
    /// <remarks>
    /// <b>Mirrors the <c>switch</c> in <c>gcode_compatibility_report_constexpr</c> exactly</b>,
    /// including that MINI, XL, iX and the INDX machines have no backwards compatibility at all.
    /// The recursion firmware does with <c>upgrade_from</c> is the loop in <see cref="CanPrint"/>.
    /// </remarks>
    private static PrinterModelGroup UpgradesFrom(PrinterModelGroup group)
    {
        return group switch
        {
            PrinterModelGroup.Xlp => PrinterModelGroup.Xl,
            PrinterModelGroup.Mk3_5 => PrinterModelGroup.Mk3,
            PrinterModelGroup.Mk4 => PrinterModelGroup.Mk3,
            PrinterModelGroup.Mk4S => PrinterModelGroup.Mk4,
            PrinterModelGroup.CoreOne => PrinterModelGroup.Mk4S,
            PrinterModelGroup.CoreOneL => PrinterModelGroup.CoreOne,
            _ => PrinterModelGroup.Unknown,
        };
    }
}
