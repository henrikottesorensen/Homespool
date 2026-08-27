using System;
using System.ComponentModel.DataAnnotations;

namespace Homespool.Model.Entities;

/// <summary>
/// What was on one printer's drive the last time it said - the whole listing, replaced each time a
/// new one arrives.
/// </summary>
/// <remarks>
/// <para>
/// <b>A snapshot, so it is upserted and never appended to.</b> A directory listing is wholly
/// superseded by the next one, which makes it the shape <see cref="PrinterLiveState"/> has rather
/// than the shape <c>PrinterEvent</c> has. It lived in the
/// event log until 2026-08-26 and that is what made events unbounded: firmware puts print files in
/// the drive root, so one listing is the entire drive and only ever grows.
/// </para>
/// <para>
/// <b>Not <see cref="PrintFileOnPrinter"/>, which is the confusable neighbour.</b> That table is
/// <i>our</i> files as they exist on a drive, keyed to a <c>PrintFile</c> we sent. This is everything
/// on the drive, including what was put there by hand, by another slicer, or before the printer was
/// ever enrolled. Same subject, different set; do not merge them.
/// </para>
/// <para>
/// <b>A belief, like its neighbour.</b> The drive is the truth and we only hear about it when the
/// printer volunteers a listing, so this is as stale as the last <c>FILE_INFO</c> - see
/// <see cref="PrintFileOnPrinter"/>'s own remarks on the same point.
/// </para>
/// </remarks>
public class PrinterDriveListing
{
    /// <summary>The printer whose drive this describes. 1:1, sharing the primary key.</summary>
    [Key]
    public int PrinterId { get; set; }

    /// <summary>When the listing this row holds was received.</summary>
    public DateTimeOffset TakenAt { get; set; }

    /// <summary>
    /// How many entries the printer reported, from the wire's own <c>file_count</c>.
    /// </summary>
    /// <remarks>
    /// The printer's count rather than a count of <see cref="Entries"/>, so the two disagreeing is
    /// visible instead of hidden - and so the number survives even where the entries were not stored.
    /// </remarks>
    public int FileCount { get; set; }

    /// <summary>
    /// The listing itself, verbatim as the wire's <c>children</c> array, or null where the printer
    /// reported a count and no entries.
    /// </summary>
    /// <remarks>
    /// Raw JSON for the same reason <c>PrinterEvent.Payload</c> is: the entry shape is firmware's,
    /// not ours, and exploding it into columns would need a migration every time a release adds a
    /// field. Nothing reads it today - it is stored because it is a fact already received and this is
    /// the shape that can express its supersession, not because a screen wants it.
    /// </remarks>
    public string? Entries { get; set; }
}
