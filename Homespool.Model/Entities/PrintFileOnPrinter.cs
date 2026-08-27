using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Homespool.Model.Entities;

/// <summary>
/// One of our files, as it exists on one printer's drive: whether it has arrived, and what the
/// printer calls it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keyed on <i>(file, printer)</i> rather than on a queue entry, and that is the design.</b>
/// Transfer belongs to the pair, not to the intention: two queued
/// prints of one file on one printer must move the bytes once, and reordering the queue past a file
/// already sent must leave it sitting on the drive rather than re-sending it. A row here outlives
/// every <see cref="QueuedPrint"/> that caused it.
/// </para>
/// <para>
/// <b>It is a cache of the printer's drive, not an authority over it.</b> The drive is the truth and
/// we cannot see it without asking - a person can delete a file at the panel, and a card can be
/// swapped. So a row saying <see cref="Arrived"/> is a belief, and the loop must treat
/// <c>File not found</c> from a <c>START_PRINT</c> as the drive correcting us rather than as an
/// impossibility.
/// </para>
/// <para>
/// <b>The name is deliberately literal.</b> It was <c>PrinterFileCopy</c> and then
/// <c>PrintFileReplica</c> (Henrik, 2026-08-02); the first does not parse unambiguously - there is no
/// <c>PrinterFile</c> type - and the second needed a paragraph explaining that nothing replicates.
/// A name that has to say what it does not mean is worse than a plain one. This says which file and
/// where it is, and nothing else, which is the whole content of the row.
/// </para>
/// </remarks>
public class PrintFileOnPrinter
{
    public long Id { get; set; }

    /// <summary>The printer whose drive this describes.</summary>
    /// <remarks>
    /// <b>The key alone, with no navigation</b> (2026-08-04) - see
    /// <see cref="QueuedPrint.PrinterId"/>, which lost the same slot for the same reason and on the
    /// same evidence: nothing ever requested it, so fix-up was its only writer.
    /// </remarks>
    public int PrinterId { get; set; }

    public long PrintFileId { get; set; }

    [ForeignKey(nameof(PrintFileId))]
    public virtual PrintFile? PrintFile { get; set; }

    /// <summary>
    /// When the transfer was started, or null if none has been. Set back to null when a transfer
    /// ends, so a non-null value means "in flight right now".
    /// </summary>
    /// <remarks>
    /// A timestamp rather than a flag because it is also the retry clock: firmware has a single
    /// system-wide transfer slot, so a busy signal is answered by waiting, and knowing <i>how long</i>
    /// is what distinguishes waiting from wedged. It is not authoritative either - a server restart
    /// mid-transfer leaves this set with nothing running, which is why the loop treats an old value as
    /// stale rather than as a transfer to keep waiting on.
    /// </remarks>
    public DateTimeOffset? TransferStartedAt { get; set; }

    /// <summary>
    /// Why this file cannot be sent to this printer, or null when nothing is in the way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Here rather than on the queue entry, because <see cref="QueuedPrint"/> is property-less by
    /// design.</b> A block is not the intention changing - somebody still wants this printed - it is
    /// the loop's own bookkeeping about a <i>(file, printer)</i> pair, which is exactly what this row
    /// is for. The entry stays in the queue untouched.
    /// </para>
    /// <para>
    /// <b>And the queue holds behind it, like a spooler</b> (Henrik, 2026-08-03: *"Holds, like a
    /// traditional printer spooler"*) - which is what lpd and CUPS have always done with a job that
    /// cannot proceed. The alternative, skipping past it, keeps the printer busy but makes the queue
    /// stop being the order people can see, and a shared queue that silently rearranges is worse than
    /// one that visibly stops with a reason attached.
    /// </para>
    /// <para>
    /// <b>A code, not a sentence.</b> It used to hold finished English, which meant the column could
    /// not be translated and that the free-space check had to recognise its own holds by matching
    /// its own opening words. <see cref="PrintHoldReason"/> carries what happened; the words are
    /// chosen when somebody reads the page, in whatever language they read.
    /// </para>
    /// </remarks>
    public PrintHoldReason? HoldReason { get; set; }

    /// <summary>
    /// Free space the printer reported when the hold was set, in bytes. Null unless
    /// <see cref="HoldReason"/> is <see cref="PrintHoldReason.InsufficientSpace"/>.
    /// </summary>
    /// <remarks>
    /// Stored rather than re-asked, because it is an observation made at the moment of the hold. A
    /// fresh question would cost a round trip and could answer differently from the hold the reader
    /// is looking at, which would make the page contradict itself.
    /// </remarks>
    public long? HoldPrinterFreeBytes { get; set; }

    /// <summary>
    /// The size of the colliding file as the printer reported it, in bytes. Null unless
    /// <see cref="HoldReason"/> is <see cref="PrintHoldReason.FileExistsDifferentSize"/>.
    /// </summary>
    /// <remarks>
    /// <b>Our own size is not stored beside it</b> - it is on the <see cref="PrintFile"/> this row
    /// already points at, and duplicating it would let the two disagree.
    /// </remarks>
    public long? HoldPrinterFileBytes { get; set; }

    /// <summary>When the block was last confirmed - so a held queue need not re-ask every tick.</summary>
    /// <remarks>
    /// Blocks clear by themselves: the loop re-checks, and a person who frees space sees the queue
    /// resume without pressing anything. This timestamp only stops that costing one command every few
    /// seconds for as long as the block lasts.
    /// </remarks>
    public DateTimeOffset? BlockedAt { get; set; }

    /// <summary>When the printer reported the transfer finished. Null until it has.</summary>
    public DateTimeOffset? ArrivedAt { get; set; }

    /// <summary>Whether the bytes are believed to be on the drive.</summary>
    public bool Arrived => ArrivedAt is not null;

    /// <summary>
    /// The path the <b>printer</b> reported for this file, which is what a print command must use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not the path we transferred to.</b> Connect transfers to the long name and then starts the
    /// print with the 8.3 name out of the answering <c>FILE_INFO</c> - observed twice in the Core One
    /// capture (<c>/usb/CALICA~3.BGC</c>, then <c>/usb/CALICA~5.BGC</c>), so it is a habit rather than
    /// a one-off. The long name may well work; the reference implementation declines to rely on it,
    /// and deriving an 8.3 name ourselves would mean inventing the <c>~N</c> collision index against
    /// directory contents we cannot see, where a wrong guess prints a different file.
    /// </para>
    /// <para>
    /// Null until a <c>FILE_INFO</c> has named it, which is the second half of a completed transfer
    /// and the reason arrival is not simply <c>TRANSFER_FINISHED</c>.
    /// </para>
    /// </remarks>
    public string? PrinterPath { get; set; }
}
