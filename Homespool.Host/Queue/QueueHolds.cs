using System;

using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Host.Exceptions;
using Homespool.Host.Printing;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Queue;

/// <summary>
/// The queue's writes about a file that cannot go to a printer: a hold, the count behind one, and the
/// one history row each hold leaves. Shared by the send and the end of a queued transfer, which hold
/// for the same reasons in the same words, and by the start of a print.
/// </summary>
internal sealed class QueueHolds
{
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>Writes holds with <paramref name="timeProvider"/>'s clock, and says so in <paramref name="logger"/>.</summary>
    /// <param name="timeProvider">The loop's clock.</param>
    /// <param name="logger">The advancer's log, which is where a person reads why their queue stopped.</param>
    public QueueHolds(TimeProvider timeProvider, ILogger logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Lifts a hold, and clears what was recorded about it.
    /// </summary>
    /// <remarks>
    /// One place, used by the loop and by queueing a file again alike, because a hold is several
    /// fields rather than one: a stale byte count or refusal left behind a cleared reason would put
    /// words on a page that describe nothing, and a count left at its bound would hold again at once.
    /// </remarks>
    public static void ClearHold(FileOnPrinter onPrinter)
    {
        onPrinter.HoldReason = null;
        onPrinter.HoldPrinterFreeBytes = null;
        onPrinter.HoldPrinterFileBytes = null;
        onPrinter.BlockedAt = null;
        TransferRetryRules.Forget(onPrinter);
        PrintStartRetryRules.Forget(onPrinter);
        PrinterDriveCopies.ForgetPathAsks(onPrinter);
    }

    /// <summary>
    /// Adds the one history row a hold leaves: the entry's file, failed at <paramref name="at"/>, in
    /// <paramref name="recorded"/>'s words, and marked as the record of <paramref name="hold"/>. Not
    /// saved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written on the transition into the hold, in the same save</b>, so history shows the file and
    /// why where a person looking back for their print will look. A row per re-check would turn it
    /// into a log, and the queue entry itself stays: somebody still wants this printed.
    /// </para>
    /// <para>
    /// <b>The mark is what keeps the hold to one notification.</b> The hold itself is announced, to
    /// everybody who can see the queue, in the queue's own sentence; a row closed as failed is
    /// otherwise announced as well, to whoever queued the file, and would tell them the same thing
    /// twice. See <see cref="PrintJob.HoldReason"/>.
    /// </para>
    /// </remarks>
    /// <param name="dbContext">The pass's context.</param>
    /// <param name="printerId">The printer whose queue is held.</param>
    /// <param name="head">The entry the queue is held behind.</param>
    /// <param name="hold">The hold being entered.</param>
    /// <param name="recorded">What happened, in English or in the printer's own words.</param>
    /// <param name="at">The moment, which is both the row's start and its end: nothing printed.</param>
    public static void AddHoldRecord(HomespoolDbContext dbContext,
                                     int printerId,
                                     QueuedPrint head,
                                     PrintHoldReason hold,
                                     string? recorded,
                                     DateTimeOffset at)
    {
        dbContext.PrintJobs.Add(new PrintJob
        {
            PrinterId = printerId,
            PrintUuid = head.PrintUuid,
            FileName = head.File!.Name,
            Digest = head.File.Digest,
            QueuedByUserId = head.QueuedByUserId,
            QueuedByScope = head.QueuedByScope,
            StartedAt = at,
            EndedAt = at,
            State = PrintState.Failed,
            Reason = recorded,
            HoldReason = hold,
        });
    }

    /// <summary>
    /// Counts a refusal that says something about this file, and holds the queue once the printer has
    /// given the same answer <see cref="RefusalRetries.HoldAfter"/> times running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Any code counts, including ones nobody has seen.</b> The default for an unrecognised refusal
    /// is still to retry - a string nobody has read is not grounds for throwing a print away - and
    /// this is what stops that default running for ever. Until the bound, the snapshot reports
    /// <see cref="QueueWaitReason.TransferRetrying"/> for the wait <see cref="RefusalRetries.WaitAfter"/>
    /// sets, so the next attempt is spaced rather than immediate.
    /// </para>
    /// <para>
    /// <b>History gets one row, on the transition</b>, carrying the printer's own words, as the space
    /// hold does. The rules never route this hold back here, so the transition happens once per hold.
    /// </para>
    /// <para>
    /// <b>Aborts are counted here too</b>, as <see cref="TransferRetryRules.TransferAbortedCode"/> with no
    /// text, and hold as <see cref="PrintHoldReason.TransferAborted"/> - a printer that takes a file and
    /// abandons it every time is bounded the way one refusing it every time is.
    /// </para>
    /// </remarks>
    public void RecordRefusal(HomespoolDbContext dbContext,
                               int printerId,
                               QueuedPrint head,
                               FileOnPrinter onPrinter,
                               string? code,
                               string? reason,
                               PrintHoldReason holdAs)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        int count = TransferRetryRules.CountAfter(onPrinter, code, reason);

        onPrinter.TransferRefusalCount = count;
        onPrinter.TransferRefusedAt = now;
        onPrinter.TransferRefusalCode = RefusalRetries.Bound(code, FileOnPrinter.TransferRefusalCodeMaxLength);
        onPrinter.TransferRefusalReason = RefusalRetries.Bound(reason, FileOnPrinter.TransferRefusalReasonMaxLength);

        if (count < RefusalRetries.HoldAfter)
        {
            _logger.LogDebug("[{PrinterId}] {Code} {Count} of {HoldAfter} for {FileName}; trying again in {Wait}",
                             printerId, LogText.Clean(onPrinter.TransferRefusalCode), count, RefusalRetries.HoldAfter,
                             head.File!.Name, RefusalRetries.WaitAfter(count));

            return;
        }

        onPrinter.HoldReason = holdAs;
        onPrinter.HoldPrinterFreeBytes = null;
        onPrinter.HoldPrinterFileBytes = null;
        onPrinter.BlockedAt = now;

        // The printer's words, not a sentence of ours: PrintJob.Reason records what was said at the
        // time, and HandleRefusalAsync writes a refused print's reason the same way.
        AddHoldRecord(dbContext, printerId, head, holdAs,
                      onPrinter.TransferRefusalReason ?? onPrinter.TransferRefusalCode, now);

        if (holdAs == PrintHoldReason.TransferAborted)
        {
            _logger.LogWarning(
                "[{PrinterId}] gave up the transfer of {FileName} {Count} times running; holding the queue " +
                "until somebody cancels or re-queues it.",
                printerId, head.File!.Name, count);

            return;
        }

        _logger.LogWarning(
            "[{PrinterId}] refused the transfer of {FileName} {Count} times running with the same answer, " +
            "{Reason} [{MachineReason}]; holding the queue until somebody cancels or re-queues it.",
            printerId, head.File!.Name, count, LogText.Clean(onPrinter.TransferRefusalReason),
            LogText.Clean(onPrinter.TransferRefusalCode));
    }

    /// <summary>
    /// Counts a refused <c>START_PRINT</c> the loop would otherwise retry, and holds the queue once the
    /// printer has refused the same way <see cref="RefusalRetries.HoldAfter"/> times running.
    /// </summary>
    /// <returns>Whether this refusal held the queue.</returns>
    /// <remarks>
    /// <para>
    /// <b>The print row is the caller's</b>, because what becomes of it is the difference between the
    /// two outcomes: a retry leaves no trace in history, and the hold leaves exactly one, the refused
    /// print closed as failed in the printer's own words.
    /// </para>
    /// <para>
    /// Until the bound, the snapshot reports <see cref="QueueWaitReason.PrintRetrying"/> for the wait
    /// <see cref="RefusalRetries.WaitAfter"/> sets, so the next attempt is spaced rather than sent on
    /// the next pass.
    /// </para>
    /// </remarks>
    public bool RecordStartRefusal(int printerId, QueuedPrint head, FileOnPrinter onPrinter, string? reason)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        int count = PrintStartRetryRules.CountAfter(onPrinter, reason);

        onPrinter.StartRefusalCount = count;
        onPrinter.StartRefusedAt = now;
        onPrinter.StartRefusalReason = RefusalRetries.Bound(reason, FileOnPrinter.TransferRefusalReasonMaxLength);

        if (count < RefusalRetries.HoldAfter)
        {
            _logger.LogDebug("[{PrinterId}] not printing {FileName} yet: {Reason} ({Count} of {HoldAfter}); trying again in {Wait}",
                             printerId, head.File!.Name, LogText.Clean(onPrinter.StartRefusalReason), count,
                             RefusalRetries.HoldAfter, RefusalRetries.WaitAfter(count));

            return false;
        }

        onPrinter.HoldReason = PrintHoldReason.PrintRefused;
        onPrinter.HoldPrinterFreeBytes = null;
        onPrinter.HoldPrinterFileBytes = null;
        onPrinter.BlockedAt = now;

        _logger.LogWarning(
            "[{PrinterId}] refused to start printing {FileName} {Count} times running with the same answer, " +
            "{Reason}; holding the queue until somebody cancels or re-queues it.",
            printerId, head.File!.Name, count, LogText.Clean(onPrinter.StartRefusalReason));

        return true;
    }

    /// <summary>
    /// Holds the queue behind a file that arrived and that the printer would not name, however long it
    /// was asked.
    /// </summary>
    /// <remarks>
    /// A print has to be started by the printer's own name for the file, and guessing it could print
    /// a different file: see <see cref="PrintHoldReason.PrinterPathUnknown"/>. History gets one row, as
    /// for the other holds, in English - the column records what happened, the banner says it in the
    /// reader's language.
    /// </remarks>
    public void HoldPathUnknown(HomespoolDbContext dbContext, int printerId, QueuedPrint head, FileOnPrinter onPrinter)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        onPrinter.HoldReason = PrintHoldReason.PrinterPathUnknown;
        onPrinter.HoldPrinterFreeBytes = null;
        onPrinter.HoldPrinterFileBytes = null;
        onPrinter.BlockedAt = now;

        string recorded = $"{head.File!.Name} arrived on the printer, which never said what it called the file.";

        AddHoldRecord(dbContext, printerId, head, PrintHoldReason.PrinterPathUnknown, recorded, now);

        _logger.LogWarning("[{PrinterId}] {Reason} Holding the queue until somebody cancels or re-queues it.",
                           printerId, recorded);
    }

    /// <summary>
    /// Holds the queue behind a file whose transfer somebody stopped at the printer.
    /// </summary>
    /// <remarks>
    /// A person's decision rather than a fault, so nothing re-attempts it: see
    /// <see cref="PrintHoldReason.TransferStopped"/>. History gets one row, as for the other holds,
    /// in English - the column records what happened, the banner says it in the reader's language.
    /// </remarks>
    public void HoldStopped(HomespoolDbContext dbContext, int printerId, QueuedPrint head, FileOnPrinter onPrinter)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        onPrinter.HoldReason = PrintHoldReason.TransferStopped;
        onPrinter.HoldPrinterFreeBytes = null;
        onPrinter.HoldPrinterFileBytes = null;
        onPrinter.BlockedAt = now;
        TransferRetryRules.Forget(onPrinter);

        string recorded = $"The transfer of {head.File!.Name} was stopped at the printer.";

        AddHoldRecord(dbContext, printerId, head, PrintHoldReason.TransferStopped, recorded, now);

        _logger.LogWarning("[{PrinterId}] {Reason} Holding the queue until somebody cancels or re-queues it.",
                           printerId, recorded);
    }

    /// <summary>
    /// Holds the queue behind a file too large for a printer to be sent.
    /// </summary>
    /// <remarks>
    /// A fact about the file rather than a fault, so nothing re-attempts it: see
    /// <see cref="PrintHoldReason.FileTooLarge"/>. History gets one row, as for the other holds, in
    /// English - the column records what happened, the banner says it in the reader's language.
    /// </remarks>
    public void HoldTooLarge(HomespoolDbContext dbContext, int printerId, QueuedPrint head, FileOnPrinter onPrinter)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        onPrinter.HoldReason = PrintHoldReason.FileTooLarge;
        onPrinter.HoldPrinterFreeBytes = null;
        onPrinter.HoldPrinterFileBytes = null;
        onPrinter.BlockedAt = now;
        TransferRetryRules.Forget(onPrinter);

        string recorded = $"{head.File!.Name} is 4 GiB or more, which is larger than a printer can be sent.";

        AddHoldRecord(dbContext, printerId, head, PrintHoldReason.FileTooLarge, recorded, now);

        _logger.LogWarning("[{PrinterId}] {Reason} Holding the queue until somebody cancels or re-queues it.",
                           printerId, recorded);
    }

    /// <summary>
    /// Holds the queue behind a file that is still in storage and could not be opened to send it, or
    /// whose owner's storage is not there to look in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Held rather than retried like a busy printer</b>: a file that cannot be opened now will not
    /// open five seconds later either, and a retry every tick would go on for as long as the fault
    /// lasted with nothing on the page to say why the queue has not moved.
    /// </para>
    /// <para>
    /// <b>The hold lifts itself</b> - the rules send it back through the transfer path every
    /// <see cref="QueueAdvancer.BlockRecheckAfter"/>, and the first send that opens the file clears
    /// it. So a second failure only moves <see cref="FileOnPrinter.BlockedAt"/> on, and history
    /// gets one row, on the transition, as the space hold writes one.
    /// </para>
    /// </remarks>
    public void HoldUnreadable(HomespoolDbContext dbContext,
                                int printerId,
                                QueuedPrint head,
                                FileOnPrinter onPrinter,
                                PrintFileUnreadableException? unreadable)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        bool newlyHeld = onPrinter.HoldReason != PrintHoldReason.FileUnreadable;

        onPrinter.HoldReason = PrintHoldReason.FileUnreadable;
        onPrinter.HoldPrinterFreeBytes = null;
        onPrinter.HoldPrinterFileBytes = null;
        onPrinter.BlockedAt = now;

        if (!newlyHeld)
        {
            return;
        }

        // English, like the space hold's record, and for the same reason: the column holds what was
        // said at the time, and the live hold is what a reader acts on, in their own language.
        string recorded = $"{head.File!.Name} could not be read from this server's storage to send it to the printer.";

        AddHoldRecord(dbContext, printerId, head, PrintHoldReason.FileUnreadable, recorded, now);

        _logger.LogWarning(unreadable, "[{PrinterId}] {Reason} The queue holds, and tries it again every {Recheck}.",
                           printerId, recorded, QueueAdvancer.BlockRecheckAfter);
    }
}
