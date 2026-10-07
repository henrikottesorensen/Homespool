using System;
using System.Collections.Generic;
using System.Linq;

using Homespool.Model;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// The nozzle and bed temperatures to preheat to for a filament type.
/// </summary>
/// <remarks>
/// <para>
/// <b>Taken from firmware's own table</b>, <c>src/common/filament_presets.cpp</c> at the pinned
/// ref - not from a slicer profile and not from memory. The point is that the
/// numbers match what the printer's own preheat menu would have chosen, so preheating from here and
/// preheating at the panel do not disagree.
/// </para>
/// <para>
/// <b>A subset, deliberately</b> (Henrik, 2026-08-07): PLA, PETG, ABS, PC, PA, FLEX. ASA is close
/// enough to ABS to not earn a separate row, and HIPS, PP and PVB are rare enough that a longer
/// dropdown costs more than they add. Adding one is a line here and nothing else.
/// </para>
/// <para>
/// <b>PA is the one model-dependent entry.</b> Firmware has
/// <c>PRINTER_IS_PRUSA_MINI() ? 280 : 285</c>, and that is not cosmetic - a MINI's maximum nozzle
/// temperature is lower, so 285 is a target it would refuse. Mirrored here rather than rounded off.
/// </para>
/// </remarks>
public sealed record FilamentPreset(string Name, int NozzleTemperature, int BedTemperature)
{
    /// <summary>What a MINI reports as its model, for the one entry that differs.</summary>
    private const string MiniModel = "MINI";

    private static readonly IReadOnlyList<FilamentPreset> Standard =
    [
        new("PLA", 215, 60),
        new("PETG", 230, 85),
        new("ABS", 255, 100),
        new("PC", 275, 100),
        new("PA", 285, 100),
        new("FLEX", 240, 50),
    ];

    /// <summary>
    /// The presets to offer for a printer, given the model it reports.
    /// </summary>
    /// <param name="model">
    /// The printer's reported model - <c>INFO</c>'s <c>printer_type</c>, so <c>2.1.0</c> rather than
    /// <c>MINI</c>; a designation is understood too. Null or unrecognised gets the standard table,
    /// which is right for everything except a MINI's PA.
    /// </param>
    public static IReadOnlyList<FilamentPreset> For(string? model)
    {
        if (!IsMini(model))
        {
            return Standard;
        }

        return Standard
               .Select(preset => preset.Name == "PA" ? preset with { NozzleTemperature = 280 } : preset)
               .ToList();
    }

    /// <summary>
    /// Finds a preset by name for a printer, or null when the name is not one this offers.
    /// </summary>
    /// <remarks>
    /// Resolved against the list rather than parsed, so a caller cannot ask for a temperature by
    /// naming one: the name selects a row, and the row carries the numbers.
    /// </remarks>
    public static FilamentPreset? Find(string? model, string? name)
    {
        return For(model).FirstOrDefault(preset => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether the reported model is a MINI, resolved rather than matched raw.
    /// </summary>
    /// <remarks>
    /// <b>A MINI reports <c>2.1.0</c>, so matching on the string as it arrives never fires</b> - and
    /// that failure is the one that costs something: the standard table's 285 °C is a target a
    /// MINI's hotend will not reach, so the entry that exists precisely because the machine is
    /// different would be the one entry never applied to it.
    /// </remarks>
    private static bool IsMini(string? model)
    {
        return PrinterModelDesignation.Of(model) is string designation &&
               designation.Contains(MiniModel, StringComparison.OrdinalIgnoreCase);
    }
}
