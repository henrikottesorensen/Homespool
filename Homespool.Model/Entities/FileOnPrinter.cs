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
/// <b>The name is deliberately literal.</b> It says which file and where it is, and nothing else,
/// which is the whole content of the row. Nothing here replicates or copies; a name implying either
/// would need a paragraph explaining that it does not.
/// </para>
/// </remarks>
public class FileOnPrinter
{
    /// <summary>Longest refusal text kept from the printer; anything past it is cut.</summary>
    /// <remarks>
    /// The text is the printer's to choose, so its length is too. Firmware's own refusals are a few
    /// words, and a bound well above that costs nothing while keeping a misbehaving client from
    /// writing a page-sized string into a row that every queue read touches.
    /// </remarks>
    public const int TransferRefusalReasonMaxLength = 256;

    /// <summary>Longest refusal code kept from the printer; anything past it is cut.</summary>
    public const int TransferRefusalCodeMaxLength = 64;

    public long Id { get; set; }

    /// <summary>The printer whose drive this describes.</summary>
    /// <remarks>
    /// <b>The key alone, with no navigation</b> (2026-08-04) - see
    /// <see cref="QueuedPrint.PrinterId"/>, which lost the same slot for the same reason and on the
    /// same evidence: nothing ever requested it, so fix-up was its only writer.
    /// </remarks>
    public int PrinterId { get; set; }

    public long FileId { get; set; }

    [ForeignKey(nameof(FileId))]
    public virtual HSFile? File { get; set; }

    /// <summary>
    /// When the transfer was started, or null if none has been. Set back to null when the printer
    /// reports the transfer ended, or when a send failed in a way that leaves nothing to fetch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A record that a transfer was started, not proof that one is running.</b> It is written
    /// before the command, and a command the printer did not answer in time leaves it set, because
    /// that says nothing either way. Whether the transfer is running is an observation the queue makes
    /// beside it: the printer having reported the file (<see cref="PrinterPath"/>), or an offer of it
    /// still standing.
    /// </para>
    /// <para>
    /// A timestamp rather than a flag because it is also the bound: a report that never comes must
    /// not wedge a queue, so an old value is treated as stale rather than as a transfer to keep
    /// waiting on.
    /// </para>
    /// </remarks>
    public DateTimeOffset? TransferStartedAt { get; set; }

    /// <summary>
    /// The id of the command whose transfer this row is waiting to hear the end of, or null when it
    /// is waiting on none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one thing that ties an ending to an attempt.</b> Firmware's <c>TRANSFER_FINISHED</c>,
    /// <c>TRANSFER_ABORTED</c> and <c>TRANSFER_STOPPED</c> carry nothing about the file - only the
    /// <c>start_cmd_id</c> of the command that started the transfer - so an ending settles the row
    /// holding that id, and nothing else.
    /// </para>
    /// <para>
    /// <b>Cleared in the same save as the ending's effect</b>, which is what makes reading the event
    /// log twice harmless: an ending already applied names an attempt no row is waiting on any more,
    /// and so does the ending of an attempt a later one replaced. Written when the printer takes the
    /// transfer or leaves the offer unanswered, by both the queue and a direct send.
    /// </para>
    /// <para>
    /// Firmware's ids are 32-bit; stored widened, as <see cref="PrinterEvent.CommandId"/> is.
    /// </para>
    /// </remarks>
    public long? TransferCommandId { get; set; }

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
    /// <b>Our own size is not stored beside it</b> - it is on the <see cref="HSFile"/> this row
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

    /// <summary>
    /// How many times running the printer has refused this transfer with the same answer, or null
    /// when the last attempt was not refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Consecutive and identical, not a total.</b> A refusal whose code or text differs from the
    /// one recorded starts the count again at one, because a changing answer means the situation is
    /// moving and a retry may yet get through. An answer that never changes is the signal, and at
    /// <c>RefusalRetries.HoldAfter</c> the queue holds with
    /// <see cref="PrintHoldReason.TransferRefused"/>.
    /// </para>
    /// <para>
    /// <b>The busy transfer slot is not counted</b>, and neither is anything that was not an answer -
    /// a timeout, a dropped connection. Those say nothing about this file, and a long transfer of
    /// somebody else's would otherwise trip the hold on a queue that only had to wait.
    /// </para>
    /// </remarks>
    public int? TransferRefusalCount { get; set; }

    /// <summary>When the printer last refused this transfer; the clock the next attempt waits on.</summary>
    public DateTimeOffset? TransferRefusedAt { get; set; }

    /// <summary>
    /// The printer's machine-readable reason for the last refusal, such as <c>STORAGE_FAILURE</c>, or
    /// null when it sent none.
    /// </summary>
    public string? TransferRefusalCode { get; set; }

    /// <summary>
    /// The printer's own words for the last refusal, kept as it sent them apart from the length bound.
    /// </summary>
    /// <remarks>
    /// <b>Not translated, and not meant to be.</b> <see cref="HoldReason"/> is the sentence a reader
    /// acts on, in their language; this is the firmware's string beside it, quoted, the same split
    /// <see cref="PrintJob.Reason"/> makes for a refused print. It is text from a printer, so anything
    /// that renders it must encode it and anything that logs it must clean it.
    /// </remarks>
    public string? TransferRefusalReason { get; set; }

    /// <summary>
    /// How many times running the printer has refused to start printing this file with the same
    /// words, or null when the last <c>START_PRINT</c> of it was not refused that way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="TransferRefusalCount"/>'s bound, for the other command the queue sends about a
    /// file.</b> A refusal the loop does not recognise - <c>Can't print now</c> above all, which
    /// firmware also says when <c>print_begin</c> does not take - is retried rather than treated as
    /// terminal, and an answer that never changes would be retried for ever. Consecutive and identical
    /// for the same reason as there; at <c>RefusalRetries.HoldAfter</c> the queue holds with
    /// <see cref="PrintHoldReason.PrintRefused"/>.
    /// </para>
    /// <para>
    /// <b>Columns of their own rather than the transfer's.</b> Sharing them would let a transfer's
    /// refusal and a print's restart each other's count, and a hold would have to work out which
    /// command it was about from what was stored.
    /// </para>
    /// </remarks>
    public int? StartRefusalCount { get; set; }

    /// <summary>When the printer last refused to start this file; the clock the next attempt waits on.</summary>
    public DateTimeOffset? StartRefusedAt { get; set; }

    /// <summary>
    /// The printer's own words for the last refused start, kept as it sent them apart from the
    /// length bound.
    /// </summary>
    /// <remarks>
    /// <c>START_PRINT</c> refusals carry no machine-readable code, only these words - so they are both
    /// what is compared and what a reader is shown. Text from a printer, rendered encoded and logged
    /// cleaned, as <see cref="TransferRefusalReason"/> is.
    /// </remarks>
    public string? StartRefusalReason { get; set; }

    /// <summary>
    /// How many times the printer has been asked what it calls this file, since it arrived without
    /// saying, and has not told us; or null when no ask is outstanding.
    /// </summary>
    /// <remarks>
    /// <b>A count of asks rather than a deadline from <see cref="ArrivedAt"/></b>, because only asking
    /// is evidence: a printer that was off has not been asked, and a file queued again is to be asked
    /// about afresh. At <c>QueueAdvancer.PathAsksBeforeHold</c> the queue holds with
    /// <see cref="PrintHoldReason.PrinterPathUnknown"/>; lifting a hold clears it.
    /// </remarks>
    public int? PathAskCount { get; set; }

    /// <summary>When the printer was last asked what it calls this file; the clock the next ask waits on.</summary>
    public DateTimeOffset? PathAskedAt { get; set; }

    /// <summary>When the printer reported the transfer finished. Null until it has.</summary>
    /// <remarks>
    /// <b><c>TRANSFER_FINISHED</c>, not the first <c>FILE_INFO</c></b>, which firmware sends a few
    /// seconds in, once the partial is printable. A print may start on that report - see
    /// <see cref="PrinterPath"/> - but a transfer that fails after it leaves a partial, and firmware
    /// removes that, so it is not a file on the drive.
    /// </remarks>
    public DateTimeOffset? ArrivedAt { get; set; }

    /// <summary>Whether the whole file is believed to be on the drive.</summary>
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
    /// <b>Set by the first <c>FILE_INFO</c>, while the file is still arriving</b> - firmware names the
    /// partial by the 8.3 path the finished file keeps, so a print can start on it before
    /// <see cref="ArrivedAt"/>. Null until then, and cleared when a transfer ends without finishing,
    /// because the partial it named has gone.
    /// </para>
    /// </remarks>
    public string? PrinterPath { get; set; }

    /// <summary>
    /// The long name this file was sent under on this printer's drive: its own, or with its owner's
    /// name added when another file already had it there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Recorded before the transfer</b>, because it is what the printer's own reports carry - an
    /// arriving file's <c>display_name</c>, a job started at the panel - and they have to be matched
    /// to this row rather than to another user's file of the same name. <see cref="HSFile"/>'s
    /// name will not do: it is unique only per user, and a rename in Homespool changes it while the
    /// copy on the drive keeps the name it was sent under.
    /// </para>
    /// <para>
    /// Null on a row written before this existed, which is read as the file's own name - what every
    /// transfer used then.
    /// </para>
    /// </remarks>
    public string? DriveName { get; set; }

    /// <summary>
    /// The <see cref="HSFile.Digest"/> of the bytes sent to <see cref="DriveName"/>, or null when
    /// nothing Homespool can vouch for is there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What makes the rest of this row about one version of the file.</b> An overwrite keeps the
    /// <see cref="HSFile"/> row and changes its digest, while the drive keeps the old bytes under
    /// the same name - so <see cref="Arrived"/> and <see cref="PrinterPath"/> describe the file only
    /// while this equals the file's digest. Where the two differ, the copy is an older version and is
    /// never printed as the file: it is deleted and the file sent again.
    /// </para>
    /// <para>
    /// <b>Written when the printer takes the transfer, not when it is offered.</b> A refused offer -
    /// <c>FILE_EXISTS</c> above all - put nothing on the drive, and a digest written before the answer
    /// would claim whatever was already there as these bytes. Cleared with <see cref="PrinterPath"/>
    /// when a transfer ends without finishing, and when the copy is deleted.
    /// </para>
    /// <para>
    /// <b>Null means unattributable, not unknown-but-probably-fine.</b> A file found on the drive that
    /// Homespool did not send has no digest to record, so it is never adopted as the file; and a row
    /// written before this column existed is treated the same way, which costs that file one more
    /// transfer.
    /// </para>
    /// </remarks>
    public string? Digest { get; set; }
}
