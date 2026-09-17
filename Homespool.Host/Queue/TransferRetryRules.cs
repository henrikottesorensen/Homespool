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
/// which is right for one attempt and wrong for a thousand: a printer that refuses the same file the
/// same way every few seconds is not going to change its mind by being asked again. So an identical
/// answer repeated <see cref="HoldAfter"/> times holds the queue with
/// <see cref="PrintHoldReason.TransferRefused"/>, whatever the answer was. A list of terminal
/// reasons could only cover the codes somebody had already seen.
/// </para>
/// <para>
/// <b>The waits and the bound do different jobs.</b> The waits widen the window a slow transient has
/// to clear in, cheaply - five retries across a little under four minutes. Only the bound ends the
/// loop; waiting alone just repeats the same refusal less often.
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
    /// How many identical refusals in a row hold the queue: the first, and five retries after it.
    /// </summary>
    public const int HoldAfter = 6;

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
    /// How long to wait after the first, second, third, fourth and fifth identical refusal.
    /// </summary>
    private static readonly TimeSpan[] Waits =
    [
        TimeSpan.FromSeconds(6),
        TimeSpan.FromSeconds(12),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(120),
    ];

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
    /// Compared after <see cref="Bound"/>, since the recorded text has already been through it; a
    /// refusal longer than the column would otherwise never match its own stored copy.
    /// </remarks>
    /// <param name="row">The <i>(file, printer)</i> row as it stands before this refusal.</param>
    /// <param name="code">The machine reason the printer sent this time, if any.</param>
    /// <param name="reason">The printer's words this time, if any.</param>
    public static int CountAfter(PrintFileOnPrinter row, string? code, string? reason)
    {
        ArgumentNullException.ThrowIfNull(row);

        bool same = row.TransferRefusalCount is > 0 &&
                    string.Equals(row.TransferRefusalCode,
                                     Bound(code, PrintFileOnPrinter.TransferRefusalCodeMaxLength),
                                     StringComparison.Ordinal) &&
                    string.Equals(row.TransferRefusalReason,
                                     Bound(reason, PrintFileOnPrinter.TransferRefusalReasonMaxLength),
                                     StringComparison.Ordinal);

        return same ? row.TransferRefusalCount!.Value + 1 : 1;
    }

    /// <summary>How long the next attempt waits after <paramref name="count"/> identical refusals.</summary>
    /// <param name="count">Identical refusals so far; one or more.</param>
    public static TimeSpan WaitAfter(int count)
    {
        return Waits[Math.Clamp(count, 1, Waits.Length) - 1];
    }

    /// <summary>
    /// Whether a refused transfer is still inside its wait, so the loop should not offer it yet.
    /// </summary>
    /// <param name="row">The <i>(file, printer)</i> row, or null when nothing has been tried.</param>
    /// <param name="now">The loop's clock.</param>
    public static bool IsWaiting(PrintFileOnPrinter? row, DateTimeOffset now)
    {
        return row?.TransferRefusalCount is { } count and > 0 &&
               row.TransferRefusedAt is { } refusedAt &&
               now - refusedAt < WaitAfter(count);
    }

    /// <summary>
    /// Cuts printer-supplied text to a column's bound, without splitting a surrogate pair.
    /// </summary>
    /// <param name="value">The text, or null.</param>
    /// <param name="maxLength">The bound, in UTF-16 code units, as the column counts them.</param>
    public static string? Bound(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength)
        {
            return value;
        }

        int cut = char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;

        return value[..cut];
    }

    /// <summary>Forgets every refusal recorded on a row.</summary>
    /// <remarks>
    /// Called when the transfer is accepted, when the printer answers <c>FILE_EXISTS</c> - a different
    /// answer, with a path of its own - and when a hold is lifted. Each is a fresh start for the
    /// count. A busy slot is deliberately not among them: it says nothing about this file either way.
    /// </remarks>
    /// <param name="row">The row to reset.</param>
    public static void Forget(PrintFileOnPrinter row)
    {
        ArgumentNullException.ThrowIfNull(row);

        row.TransferRefusalCount = null;
        row.TransferRefusedAt = null;
        row.TransferRefusalCode = null;
        row.TransferRefusalReason = null;
    }
}
