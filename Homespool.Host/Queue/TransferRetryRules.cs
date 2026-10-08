using System;

using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Queue;

/// <summary>
/// Decides how a refused transfer is retried: which refusals count, how long the next attempt waits,
/// and when retrying stops and the queue holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>A bound, not a classification.</b> The transfer path retries a refusal it does not recognise,
/// so an identical answer repeated <see cref="RefusalRetries.HoldAfter"/> times holds the queue with
/// <see cref="PrintHoldReason.TransferRefused"/>, whatever the answer was. A list of terminal
/// reasons could only cover the codes somebody had already seen. The attempts are spaced on
/// <see cref="RefusalRetries"/>, the schedule a refused print start shares.
/// </para>
/// <para>
/// <b>No I/O, like <see cref="QueueRules"/></b>, so the arithmetic can be tested without a printer. The
/// advancer records the result; <see cref="QueueSnapshotReader"/> reads it back so that a page and
/// the loop agree a retry is pending.
/// </para>
/// </remarks>
public static class TransferRetryRules
{
    /// <summary>
    /// Firmware's code for its single transfer slot being taken - the one refusal that is never
    /// counted.
    /// </summary>
    /// <remarks>
    /// Buddy sends it beside <see cref="TransferInProgressText"/> (<c>planner.cpp</c>,
    /// <c>handle_transfer_result</c>, at <c>v6.10.1</c>). Excluded because it says another transfer is
    /// running, not that this one cannot run - and a large transfer of somebody else's file can hold
    /// the slot for longer than the whole retry budget.
    /// </remarks>
    public const string TransferInProgressCode = "TRANSFER_IN_PROGRESS";

    /// <summary>
    /// The words both known clients use for a taken transfer slot.
    /// </summary>
    /// <remarks>
    /// <b>Matched only when no code came with them.</b> The Python SDK refuses a busy slot with exactly
    /// this text and no machine reason at all (<c>download.py</c>, <c>DownloadMgr.start</c>), so a check
    /// on the code alone would count every busy answer from a printer running it. Where a code is
    /// present the code decides, and the text is free to change.
    /// </remarks>
    public const string TransferInProgressText = "Another transfer in progress";

    /// <summary>
    /// The code recorded when the printer abandons a transfer it had taken - firmware's own event name,
    /// since <c>TRANSFER_ABORTED</c> carries no reason of its own.
    /// </summary>
    /// <remarks>
    /// Counted exactly as a refusal is, so the same waits space the attempts and the same number of them
    /// in a row holds the queue, as <see cref="PrintHoldReason.TransferAborted"/>. Stored in the refusal
    /// columns with no text beside it, which is what tells the two apart there.
    /// </remarks>
    public const string TransferAbortedCode = "TRANSFER_ABORTED";

    /// <summary>
    /// Whether a refusal only says the printer's transfer slot is taken, and so is not counted.
    /// </summary>
    /// <param name="code">The machine reason the printer sent, if any.</param>
    /// <param name="reason">The printer's own words, if any.</param>
    public static bool IsBusySlot(string? code, string? reason)
    {
        return string.IsNullOrEmpty(code) ?
            string.Equals(reason, TransferInProgressText, StringComparison.Ordinal) :
            string.Equals(code, TransferInProgressCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// The count a new refusal brings the row to: one more when it matches the refusal already
    /// recorded, one when it does not.
    /// </summary>
    /// <remarks>
    /// Compared after <see cref="RefusalRetries.Bound"/>, since the recorded text has already been
    /// through it; a refusal longer than the column would otherwise never match its own stored copy.
    /// </remarks>
    /// <param name="row">The <i>(file, printer)</i> row as it stands before this refusal.</param>
    /// <param name="code">The machine reason the printer sent this time, if any.</param>
    /// <param name="reason">The printer's words this time, if any.</param>
    public static int CountAfter(FileOnPrinter row, string? code, string? reason)
    {
        ArgumentNullException.ThrowIfNull(row);

        bool same = row.TransferRefusalCount is > 0 &&
                    string.Equals(row.TransferRefusalCode,
                                     RefusalRetries.Bound(code, FileOnPrinter.TransferRefusalCodeMaxLength),
                                     StringComparison.Ordinal) &&
                    string.Equals(row.TransferRefusalReason,
                                     RefusalRetries.Bound(reason, FileOnPrinter.TransferRefusalReasonMaxLength),
                                     StringComparison.Ordinal);

        return same ? row.TransferRefusalCount!.Value + 1 : 1;
    }

    /// <summary>
    /// Whether a refused transfer is still inside its wait, so the loop should not offer it yet.
    /// </summary>
    /// <param name="row">The <i>(file, printer)</i> row, or null when nothing has been tried.</param>
    /// <param name="now">The loop's clock.</param>
    public static bool IsWaiting(FileOnPrinter? row, DateTimeOffset now)
    {
        return row?.TransferRefusalCount is int count and > 0 &&
               row.TransferRefusedAt is DateTimeOffset refusedAt &&
               now - refusedAt < RefusalRetries.WaitAfter(count);
    }

    /// <summary>
    /// Whether the count on a row is of aborts - transfers the printer took and gave up - rather than
    /// refusals.
    /// </summary>
    /// <param name="row">The <i>(file, printer)</i> row, or null when nothing has been tried.</param>
    public static bool IsCountingAborts(FileOnPrinter? row)
    {
        return row?.TransferRefusalCount is > 0 &&
               string.Equals(row.TransferRefusalCode, TransferAbortedCode, StringComparison.Ordinal);
    }

    /// <summary>Forgets every refusal recorded on a row.</summary>
    /// <remarks>
    /// Called when the transfer is accepted, when the printer answers <c>FILE_EXISTS</c> - a different
    /// answer, with a path of its own - and when a hold is lifted. Each is a fresh start for the
    /// count. A busy slot is deliberately not among them: it says nothing about this file either way.
    /// <b>Nor is acceptance, for a count of aborts</b> (<see cref="IsCountingAborts"/>): every aborted
    /// attempt is accepted first, so forgetting there would restart the count on every attempt. That
    /// count ends when a transfer finishes.
    /// </remarks>
    /// <param name="row">The row to reset.</param>
    public static void Forget(FileOnPrinter row)
    {
        ArgumentNullException.ThrowIfNull(row);

        row.TransferRefusalCount = null;
        row.TransferRefusedAt = null;
        row.TransferRefusalCode = null;
        row.TransferRefusalReason = null;
    }
}
