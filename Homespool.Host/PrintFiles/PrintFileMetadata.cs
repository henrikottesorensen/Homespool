using System;
using System.Linq;

using Microsoft.EntityFrameworkCore.Query;

using Homespool.Host.PrintFiles.GCode;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.PrintFiles;

/// <summary>
/// What a print file says it was sliced for, as the columns of its row.
/// </summary>
/// <remarks>
/// <b>One mapping for every writer.</b> An upload reads the file as it lands, and
/// <see cref="PrintFileReconciler"/> reads files that were indexed without being read. The compatibility
/// check reads these columns directly, so the two must write the same row for the same bytes.
/// </remarks>
public static class PrintFileMetadata
{
    /// <summary>Writes <paramref name="metadata"/> onto <paramref name="row"/>, null meaning unreadable.</summary>
    public static void Apply(HSFile row, GCodeMetadata? metadata)
    {
        ArgumentNullException.ThrowIfNull(row);

        row.MetadataState = metadata switch
        {
            null => PrintFileMetadataState.Unreadable,
            { SaysNothing: true } => PrintFileMetadataState.Silent,
            _ => PrintFileMetadataState.Read,
        };

        row.PrinterModel = metadata?.PrinterModel;
        row.ExtruderCount = metadata?.NozzleDiameters.Count is > 0 ? metadata.NozzleDiameters.Count : null;
        row.NozzleDiameter = SharedNozzleDiameter(metadata);
        row.FilamentTypes = metadata?.FilamentTypes.Count is > 0 ? string.Join(';', metadata.FilamentTypes) : null;
        row.RequiresHardenedNozzle = metadata?.AnyFilamentAbrasive;
        row.RequiresHighFlowNozzle = metadata?.AnyNozzleHighFlow;
    }

    /// <summary>
    /// Marks <paramref name="row"/> as describing nothing, for bytes that have changed since it was
    /// read.
    /// </summary>
    /// <remarks>
    /// <b>Cleared, not kept until the re-read.</b> The compatibility check reads these columns as they
    /// stand, and one describing a replaced file could hold a queue over a model the new file was not
    /// sliced for. Unread with nothing in the columns makes it say nothing until the file has been read
    /// again.
    /// </remarks>
    public static void Forget(HSFile row)
    {
        ArgumentNullException.ThrowIfNull(row);

        row.MetadataState = PrintFileMetadataState.Unread;
        row.PrinterModel = null;
        row.ExtruderCount = null;
        row.NozzleDiameter = null;
        row.FilamentTypes = null;
        row.RequiresHardenedNozzle = null;
        row.RequiresHighFlowNozzle = null;
    }

    /// <summary>
    /// Sets every metadata column to what <paramref name="described"/> carries, for a bulk update
    /// that writes a row without loading it.
    /// </summary>
    public static void Set(UpdateSettersBuilder<HSFile> set, HSFile described)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(described);

        set.SetProperty(row => row.MetadataState, described.MetadataState)
           .SetProperty(row => row.PrinterModel, described.PrinterModel)
           .SetProperty(row => row.ExtruderCount, described.ExtruderCount)
           .SetProperty(row => row.NozzleDiameter, described.NozzleDiameter)
           .SetProperty(row => row.FilamentTypes, described.FilamentTypes)
           .SetProperty(row => row.RequiresHardenedNozzle, described.RequiresHardenedNozzle)
           .SetProperty(row => row.RequiresHighFlowNozzle, described.RequiresHighFlowNozzle);
    }

    /// <summary>
    /// The one diameter every extruder in the file expects, or null if they disagree.
    /// </summary>
    /// <remarks>
    /// <b>Disagreement is not a failure to parse; it is a toolchanger.</b> Collapsing it to the
    /// first value would compare a printer's single nozzle against whichever extruder the slicer
    /// happened to write first, which is a claim nobody can act on - so the file records no
    /// diameter, and the comparison stays quiet rather than guessing.
    /// </remarks>
    private static float? SharedNozzleDiameter(GCodeMetadata? metadata)
    {
        if (metadata is null || metadata.NozzleDiameters.Count == 0)
        {
            return null;
        }

        float first = metadata.NozzleDiameters[0];

        return metadata.NozzleDiameters.All(diameter => Math.Abs(diameter - first) < GCodeMetadata.NozzleDiameterTolerance) ?
            first :
            null;
    }
}
