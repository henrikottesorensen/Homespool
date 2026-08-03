using System;

namespace Homespool.Model.Entities;

/// <summary>
/// One print: what was printed, on what, by whose asking, and how it went. Opened when the printer
/// takes the command and closed when it stops printing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The set is <c>PrintJobs</c> and "print history" is the feature</b> (Henrik, 2026-08-03). The
/// running print is a row here like any other - a table called history holding the print happening
/// right now would be a lie about its own contents. <b>Active means <see cref="EndedAt"/> is null</b>,
/// and the history page shows the rows that ended.
/// </para>
/// <para>
/// <b>It cannot be written at completion, which is why it is opened at the start.</b> By the time a
/// print ends its <see cref="QueuedPrint"/> is long deleted, taking who queued it, which file and when
/// it began; and <see cref="FirmwareJobId"/> is only observable *during* the print, from the
/// <c>JOB_INFO</c> ack and the <c>job_id</c> telemetry carries while it runs.
/// </para>
/// <para>
/// <b>It records rather than points.</b> The file's name and digest are copied in, not referenced -
/// <c>notes/file-storage.md</c>: "History rows keep whatever they record (name, digest) as a
/// <i>record</i>, not a pointer - a deleted or renamed file dangling behind history is by design."
/// So there is deliberately no foreign key to <see cref="PrintFile"/>: renaming a file does not
/// rewrite what happened, and deleting one does not erase it.
/// </para>
/// </remarks>
public class PrintJob
{
    public long Id { get; set; }

    /// <summary>Which printer ran it.</summary>
    /// <remarks>
    /// <b>The key alone, with no navigation</b> - the same choice <see cref="PrinterEvent"/> and
    /// <see cref="TelemetrySample"/> made after the teardown testing that found the hazard: a
    /// navigation is a slot EF's relationship fix-up writes each context's tracked
    /// <see cref="Printer"/> into, which poisons any instance outliving the context that loaded it.
    /// The queue's loop holds rows across scoped work in the same shape, so the risk is the same one
    /// rather than a theoretical relative of it (Henrik, 2026-08-03).
    /// <para>
    /// Nothing needs it: history is read per printer, so a caller already knows which printer it
    /// asked about.
    /// </para>
    /// </remarks>
    public int PrinterId { get; set; }

    /// <summary>The file's name as it was when the print started. A record, not a pointer.</summary>
    public required string FileName { get; set; }

    /// <summary>
    /// The content digest at that moment, when it was known - so history can say whether two prints
    /// were of the same bytes even after the file has been replaced.
    /// </summary>
    /// <remarks>
    /// Null for a file that predates the digest column, which is the same ordinary null
    /// <see cref="PrintFile.Digest"/> carries and for the same reason.
    /// </remarks>
    public string? Digest { get; set; }

    /// <summary>Who queued it. Carried across from the queue entry, which is gone by the time this ends.</summary>
    public long QueuedByUserId { get; set; }

    /// <summary>
    /// The path the print was started with - the printer's own 8.3 name, not the one we transferred to.
    /// </summary>
    public string? PrinterPath { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When the printer stopped printing. <b>Null means this print is the active one.</b></summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>
    /// Firmware's own id for this print, from the <c>JOB_INFO</c> ack. Null if it never arrived.
    /// </summary>
    /// <remarks>
    /// <b>The second of two id spaces, mapped rather than reconciled</b> - <see cref="Id"/> is ours and
    /// this is the printer's, exactly as <c>PrinterEvent.JobId</c> is. Keeping both is what Connect
    /// does (<c>id</c> and <c>connectId</c>), and it is what makes a restart mid-print survivable:
    /// reconnecting brings telemetry carrying <c>job_id</c>, and this column is what re-attaches the
    /// row to the print already running.
    /// </remarks>
    public int? FirmwareJobId { get; set; }

    public PrintOutcome Outcome { get; set; }

    /// <summary>
    /// Whether <i>we</i> asked for the stop, as opposed to somebody pressing stop at the printer.
    /// </summary>
    /// <remarks>
    /// Recorded at the time because it cannot be recovered afterwards: a stop we sent and a stop made
    /// at the panel produce an identical state change, and <c>notes/print-queue.md</c> makes the same
    /// point about causation not being inferable from state. Same shape as the id mapping above.
    /// </remarks>
    public bool StoppedByUs { get; set; }

    /// <summary>
    /// Firmware's own words, when a print ended badly or never began - e.g. <c>Forbidden path</c>.
    /// </summary>
    /// <remarks>
    /// <b>The reason a refused print gets a row at all.</b> Before this, a terminally refused print had
    /// its queue entry dropped and a line written to a log, so from the user's side a queued print
    /// silently disappeared with nowhere to find out why. Such a row is opened and closed in the same
    /// moment - <see cref="StartedAt"/> equals <see cref="EndedAt"/> - which is honest: nothing ever
    /// printed.
    /// </remarks>
    public string? Reason { get; set; }
}
