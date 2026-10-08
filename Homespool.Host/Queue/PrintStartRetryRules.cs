using System;

using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Queue;

/// <summary>
/// Decides how a refused <c>START_PRINT</c> is retried: how the count of identical refusals moves,
/// whether the next attempt may go yet, and when the count is forgotten.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same bound as a refused transfer, for the same reason.</b> The loop retries <c>Can't print
/// now</c> and any reason it has not read, and a printer that gives the same answer every time would
/// otherwise be sent the same command every five seconds for as long as anybody left it. An identical
/// answer repeated <see cref="RefusalRetries.HoldAfter"/> times holds the queue with
/// <see cref="PrintHoldReason.PrintRefused"/>, on <see cref="RefusalRetries"/>' waits.
/// </para>
/// <para>
/// <b>Which refusals count is the advancer's</b>, beside the reasons it already classifies: only the
/// ones it would otherwise retry. A terminal reason ends the entry, <c>File not found</c> sends the
/// file again, and <c>No job in progress</c> is not a refusal at all.
/// </para>
/// <para>
/// <b>No I/O, like <see cref="TransferRetryRules"/></b>; <see cref="QueueSnapshotReader"/> reads the
/// wait back so that a page and the loop agree a retry is pending.
/// </para>
/// </remarks>
public static class PrintStartRetryRules
{
    /// <summary>
    /// The count a new refusal brings the row to: one more when its words match the refusal already
    /// recorded, one when they do not.
    /// </summary>
    /// <remarks>
    /// Compared after <see cref="RefusalRetries.Bound"/>, since the recorded text has already been
    /// through it.
    /// </remarks>
    /// <param name="row">The <i>(file, printer)</i> row as it stands before this refusal.</param>
    /// <param name="reason">The printer's words this time, if any.</param>
    public static int CountAfter(FileOnPrinter row, string? reason)
    {
        ArgumentNullException.ThrowIfNull(row);

        bool same = row.StartRefusalCount is > 0 &&
                    string.Equals(row.StartRefusalReason,
                                  RefusalRetries.Bound(reason, FileOnPrinter.TransferRefusalReasonMaxLength),
                                  StringComparison.Ordinal);

        return same ? row.StartRefusalCount!.Value + 1 : 1;
    }

    /// <summary>
    /// Whether a refused start is still inside its wait, so the loop should not command it yet.
    /// </summary>
    /// <param name="row">The <i>(file, printer)</i> row, or null when nothing has been tried.</param>
    /// <param name="now">The loop's clock.</param>
    public static bool IsWaiting(FileOnPrinter? row, DateTimeOffset now)
    {
        return row?.StartRefusalCount is int count and > 0 &&
               row.StartRefusedAt is DateTimeOffset refusedAt &&
               now - refusedAt < RefusalRetries.WaitAfter(count);
    }

    /// <summary>Forgets every refused start recorded on a row.</summary>
    /// <remarks>
    /// Called when the printer takes the print - by acknowledging it, or by turning out to be running
    /// it - and when a hold is lifted. Each is a fresh start for the count.
    /// </remarks>
    /// <param name="row">The row to reset.</param>
    public static void Forget(FileOnPrinter row)
    {
        ArgumentNullException.ThrowIfNull(row);

        row.StartRefusalCount = null;
        row.StartRefusedAt = null;
        row.StartRefusalReason = null;
    }
}
