using System;

using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Host.Exceptions;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Queue;

/// <summary>
/// The queue's writes about a file that cannot go to a printer: a hold, the count behind one, and the
/// one history row each hold leaves. Shared by the send and the end of a queued transfer, which hold
/// for the same reasons in the same words.
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
    /// One place, because a hold is several fields rather than one and leaving a stale byte count or
    /// refusal behind a cleared reason would put words on a page that describe nothing.
    /// </remarks>
    public static void ClearHold(FileOnPrinter onPrinter)
    {
        onPrinter.HoldReason = null;
        onPrinter.HoldPrinterFreeBytes = null;
        onPrinter.HoldPrinterFileBytes = null;
        onPrinter.BlockedAt = null;
        TransferRetryRules.Forget(onPrinter);
    }

    /// <summary>
    /// Counts a refusal that says something about this file, and holds the queue once the printer has
    /// given the same answer <see cref="TransferRetryRules.HoldAfter"/> times running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Any code counts, including ones nobody has seen.</b> The default for an unrecognised refusal
    /// is still to retry - a string nobody has read is not grounds for throwing a print away - and
    /// this is what stops that default running for ever. Until the bound, the snapshot reports
    /// <see cref="QueueWaitReason.TransferRetrying"/> for the wait <see cref="TransferRetryRules.WaitAfter"/>
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
        onPrinter.TransferRefusalCode = TransferRetryRules.Bound(code, FileOnPrinter.TransferRefusalCodeMaxLength);
        onPrinter.TransferRefusalReason = TransferRetryRules.Bound(reason, FileOnPrinter.TransferRefusalReasonMaxLength);

        if (count < TransferRetryRules.HoldAfter)
        {
            _logger.LogDebug("[{PrinterId}] {Code} {Count} of {HoldAfter} for {FileName}; trying again in {Wait}",
                             printerId, LogText.Clean(onPrinter.TransferRefusalCode), count, TransferRetryRules.HoldAfter,
                             head.File!.Name, TransferRetryRules.WaitAfter(count));

            return;
        }

        onPrinter.HoldReason = holdAs;
        onPrinter.HoldPrinterFreeBytes = null;
        onPrinter.HoldPrinterFileBytes = null;
        onPrinter.BlockedAt = now;

        // The printer's words, not a sentence of ours: PrintJob.Reason records what was said at the
        // time, and HandleRefusal writes a refused print's reason the same way.
        dbContext.PrintJobs.Add(new PrintJob
        {
            PrinterId = printerId,
            PrintUuid = head.PrintUuid,
            FileName = head.File!.Name,
            Digest = head.File.Digest,
            QueuedByUserId = head.QueuedByUserId,
            QueuedByScope = head.QueuedByScope,
            StartedAt = now,
            EndedAt = now,
            State = PrintState.Failed,
            Reason = onPrinter.TransferRefusalReason ?? onPrinter.TransferRefusalCode,
        });

        if (holdAs == PrintHoldReason.TransferAborted)
        {
            _logger.LogWarning(
                "[{PrinterId}] gave up the transfer of {FileName} {Count} times running; holding the queue " +
                "until somebody cancels or re-queues it.",
                printerId, head.File.Name, count);

            return;
        }

        _logger.LogWarning(
            "[{PrinterId}] refused the transfer of {FileName} {Count} times running with the same answer, " +
            "{Reason} [{MachineReason}]; holding the queue until somebody cancels or re-queues it.",
            printerId, head.File.Name, count, LogText.Clean(onPrinter.TransferRefusalReason),
            LogText.Clean(onPrinter.TransferRefusalCode));
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

        dbContext.PrintJobs.Add(new PrintJob
        {
            PrinterId = printerId,
            PrintUuid = head.PrintUuid,
            FileName = head.File.Name,
            Digest = head.File.Digest,
            QueuedByUserId = head.QueuedByUserId,
            QueuedByScope = head.QueuedByScope,
            StartedAt = now,
            EndedAt = now,
            State = PrintState.Failed,
            Reason = recorded,
        });

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

        dbContext.PrintJobs.Add(new PrintJob
        {
            PrinterId = printerId,
            PrintUuid = head.PrintUuid,
            FileName = head.File.Name,
            Digest = head.File.Digest,
            QueuedByUserId = head.QueuedByUserId,
            QueuedByScope = head.QueuedByScope,
            StartedAt = now,
            EndedAt = now,
            State = PrintState.Failed,
            Reason = recorded,
        });

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

        dbContext.PrintJobs.Add(new PrintJob
        {
            PrinterId = printerId,
            PrintUuid = head.PrintUuid,
            FileName = head.File.Name,
            Digest = head.File.Digest,
            QueuedByUserId = head.QueuedByUserId,
            QueuedByScope = head.QueuedByScope,
            StartedAt = now,
            EndedAt = now,
            State = PrintState.Failed,
            Reason = recorded,
        });

        _logger.LogWarning(unreadable, "[{PrinterId}] {Reason} The queue holds, and tries it again every {Recheck}.",
                           printerId, recorded, QueueAdvancer.BlockRecheckAfter);
    }
}
