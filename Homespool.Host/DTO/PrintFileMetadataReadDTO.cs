using System;
using System.Collections.Generic;

using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.DTO;

/// <summary>What a file says it was sliced for.</summary>
/// <remarks>
/// <para>
/// <b>Read <see cref="State"/> first: it says why the rest may be null.</b> <c>Read</c> means the
/// fields are what the file said; <c>Silent</c>, that it parsed and carried no slicer configuration,
/// which is ordinary outside PrusaSlicer; <c>Unreadable</c>, that it could not be parsed at all; and
/// <c>Unread</c>, that nobody has looked. The last three all leave every field null, and they are not
/// the same answer.
/// </para>
/// <para>
/// <b>The file's own claims, in the slicer's vocabulary.</b> Nothing here is checked against a
/// printer: queueing is what answers that, in its warnings.
/// </para>
/// </remarks>
public class PrintFileMetadataReadDTO
{
    /// <summary><c>Read</c>, <c>Silent</c>, <c>Unreadable</c> or <c>Unread</c>.</summary>
    public required string State { get; set; }

    /// <summary>
    /// The model it was sliced for, as the slicer names it - <c>COREONE</c>, <c>MK4IS</c>.
    /// </summary>
    /// <remarks>
    /// <b>Not comparable with a printer's model as a string.</b> The slicer writes <c>MK4IS</c> where
    /// firmware reports <c>MK4</c>; the two meet through a mapping, never through equality.
    /// </remarks>
    public string? PrinterModel { get; set; }

    /// <summary>
    /// The nozzle diameter it expects, in millimetres. Null when it did not say, or when its extruders
    /// disagree - which only a toolchanger's can.
    /// </summary>
    public float? NozzleDiameter { get; set; }

    /// <summary>How many extruders it was sliced for. Not the number of filaments: an MMU print lists several through one.</summary>
    public int? ExtruderCount { get; set; }

    /// <summary>Every filament the print uses, as the slicer spells them - <c>PLA</c>, <c>PETG</c>.</summary>
    public IReadOnlyList<string>? FilamentTypes { get; set; }

    /// <summary>Whether any filament is abrasive, so the nozzle it passes through must be hardened.</summary>
    public bool? RequiresHardenedNozzle { get; set; }

    /// <summary>Whether it was sliced for a high-flow hotend.</summary>
    public bool? RequiresHighFlowNozzle { get; set; }

    /// <summary>What the row records, or an unread description when there is no row.</summary>
    /// <param name="row">The file's row, or null when it has none.</param>
    public static PrintFileMetadataReadDTO From(PrintFile? row)
    {
        return new()
        {
            State = StateOf(row),
            PrinterModel = row?.PrinterModel,
            NozzleDiameter = row?.NozzleDiameter,
            ExtruderCount = row?.ExtruderCount,
            FilamentTypes = row?.FilamentTypes is { Length: > 0 } types ?
                types.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) :
                null,
            RequiresHardenedNozzle = row?.RequiresHardenedNozzle,
            RequiresHighFlowNozzle = row?.RequiresHighFlowNozzle,
        };
    }

    /// <summary>The state as the API names it.</summary>
    /// <remarks>
    /// <b>No row, and a row nobody wrote a state on, are both <c>Unread</c></b> - nobody has looked.
    /// The startup reconcile and a print's lazy index both insert rows without reading the file, and
    /// once stored those rows with no state at all; they write <c>Unread</c> now, but a database
    /// carries the old rows until each file is next uploaded.
    /// </remarks>
    private static string StateOf(PrintFile? row)
    {
        return row?.MetadataState switch
        {
            null or PrintFileMetadataState.Undefined => nameof(PrintFileMetadataState.Unread),
            PrintFileMetadataState state => state.ToString(),
        };
    }
}
