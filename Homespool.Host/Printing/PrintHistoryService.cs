using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Authorisation;
using Homespool.Host.Localisation;
using Homespool.Host.Queue;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Printing;

/// <summary>
/// What a printer has printed, and what it is printing now.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reading only.</b> Rows are written by the queue's loop, which is the only thing that knows when
/// a print starts, reaches the machine, or ends. Nothing here changes history.
/// </para>
/// <para>
/// <b>The surface this exists for is the held queue.</b> The loop can hold indefinitely on a
/// condition only a person can clear - a file that will not fit on the drive - and until something
/// read these rows that hold was indistinguishable from a queue that had simply stopped. A held queue
/// with an invisible reason is the silent stall the design rejected twice; this is what makes the
/// reason visible.
/// </para>
/// <para>
/// <c>CanRead</c>, not <c>CanUse</c>: seeing what a printer has done is the same class of thing as
/// seeing its temperature, and none of it makes the printer work.
/// </para>
/// <para>
/// <b>In <c>Services/</c> rather than <c>Queue/</c>, deliberately</b> (Henrik, 2026-08-03), even though
/// two of its three reads are pure queue business and the queue's loop writes everything it returns.
/// It is a read model rather than part of the loop, and it is expected to grow questions the queue has
/// no opinion about - what a <i>person</i> has printed, across printers, over time. Moving it beside
/// the loop would make each of those look out of place in turn. Left here so the next tidy-up does not
/// undo the decision.
/// </para>
/// </remarks>
public class PrintHistoryService
{
    /// <summary>How many finished prints a page shows without asking for more.</summary>
    private const int RecentCount = 20;

    private readonly HomespoolDbContext _dbContext;
    private readonly PrinterAccessService _access;

    /// <summary>
    /// Asked what the queue is held by, rather than reading the column directly.
    /// </summary>
    /// <remarks>
    /// <b>Because not every hold is a stored one.</b> The space and name holds are facts somebody
    /// discovered and wrote down; a file-versus-printer disagreement is recomputed on every read, so
    /// the column is empty for it and a banner reading the column alone would stay silent while the
    /// queue visibly refused to move. One builder answers "what is in the way", exactly as it does
    /// for the loop.
    /// </remarks>
    private readonly QueueSnapshotReader _snapshots;

    /// <summary>Shared with the queue, which asks the same question about whoever queued a print.</summary>
    private readonly UserNameLookup _names;

    public PrintHistoryService(HomespoolDbContext dbContext,
                               PrinterAccessService access,
                               QueueSnapshotReader snapshots,
                               UserNameLookup names)
    {
        _dbContext = dbContext;
        _access = access;
        _snapshots = snapshots;
        _names = names;
    }

    /// <summary>
    /// The print running on this printer, or null when none is.
    /// </summary>
    /// <remarks>
    /// "Running" here means <see cref="PrintJob.EndedAt"/> is null, which includes the seconds a print
    /// spends <see cref="Model.PrintState.Starting"/> before the printer reports itself printing.
    /// A page showing this should say so rather than claiming the machine is printing already.
    /// </remarks>
    public async Task<PrintJob?> GetActiveAsync(int printerId, Caller caller, CancellationToken cancellationToken)
    {
        await _access.RequireAsync(printerId, caller, Capability.ViewHistory, cancellationToken);

        return await _dbContext.PrintJobs
                               .AsNoTracking()
                               .SingleOrDefaultAsync(job => job.PrinterId == printerId && job.EndedAt == null,
                                                     cancellationToken);
    }

    /// <summary>Finished prints, newest first.</summary>
    public Task<IReadOnlyList<PrintJob>> ListAsync(int printerId,
                                                   Caller caller,
                                                   CancellationToken cancellationToken)
    {
        return ListAsync(printerId, caller, before: null, RecentCount, cancellationToken);
    }

    /// <summary>
    /// Finished prints that started before <paramref name="before"/>, newest first, at most
    /// <paramref name="limit"/> of them.
    /// </summary>
    /// <remarks>
    /// <b>The cursor is a start time rather than a handle</b>, because <see cref="PrintJob.PrintUuid"/>
    /// is not unique and so cannot say where a page ended. Two rows starting in the same instant on one
    /// printer would straddle a page boundary badly; a printer runs one print at a time, and the rows the
    /// queue writes for a hold carry the moment it was noticed, so the case is not worth a compound
    /// cursor.
    /// </remarks>
    public async Task<IReadOnlyList<PrintJob>> ListAsync(int printerId,
                                                         Caller caller,
                                                         DateTimeOffset? before,
                                                         int limit,
                                                         CancellationToken cancellationToken)
    {
        await _access.RequireAsync(printerId, caller, Capability.ViewHistory, cancellationToken);

        IQueryable<PrintJob> finished = _dbContext.PrintJobs
                                                  .AsNoTracking()
                                                  .Where(job => job.PrinterId == printerId && job.EndedAt != null);

        if (before is not null)
        {
            finished = finished.Where(job => job.StartedAt < before.Value);
        }

        return await finished.OrderByDescending(job => job.StartedAt)
                             .ThenByDescending(job => job.Id)
                             .Take(limit)
                             .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The newest print on this printer carrying the handle it was queued under, running or finished.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Keyed on <see cref="PrintJob.PrintUuid"/> rather than the row id</b>, matching the queue's
    /// own controls: it is the handle minted at enqueue and carried through every stage, so it is what
    /// a page already has to hand and the only one worth putting in a form.
    /// </para>
    /// <para>
    /// <b>The newest, because the handle is not unique.</b> A full drive or a refused transfer writes a
    /// <see cref="PrintState.Failed"/> row while the entry stays queued, and the print that follows
    /// writes another under the same handle. Every such row came from one queue entry, so they name the
    /// same file and the same person, and the latest is the one nearest the truth.
    /// </para>
    /// </remarks>
    public async Task<PrintJob?> FindAsync(int printerId,
                                           Guid printUuid,
                                           Caller caller,
                                           CancellationToken cancellationToken)
    {
        await _access.RequireAsync(printerId, caller, Capability.ViewHistory, cancellationToken);

        return await _dbContext.PrintJobs
                               .AsNoTracking()
                               .Where(job => job.PrinterId == printerId && job.PrintUuid == printUuid)
                               .OrderByDescending(job => job.StartedAt)
                               .ThenByDescending(job => job.Id)
                               .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Usernames for whoever stopped any of <paramref name="jobs"/>, keyed by user id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A lookup rather than a join, because history does not point at users.</b>
    /// <see cref="PrintJob.StoppedByUserId"/> has no foreign key and no navigation - see the entity
    /// for why - so the name is fetched for the handful of ids a page is showing rather than included
    /// per row.
    /// </para>
    /// <para>
    /// Takes rows the caller already holds, so it authorises nothing itself: reaching a
    /// <see cref="PrintJob"/> at all has been through <see cref="ListAsync(int, Caller, CancellationToken)"/> or
    /// <see cref="GetActiveAsync"/> already, and this adds no way to ask about a printer you could
    /// not already read.
    /// </para>
    /// <para>
    /// An id with no row comes back absent rather than blank, so a caller can tell "stopped by
    /// somebody we can no longer name" from "stopped at the panel" - which is
    /// <see cref="PrintJob.StoppedByUserId"/> being null, and a different fact.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<long, string>> GetStopperNamesAsync(IEnumerable<PrintJob> jobs,
                                                                              CancellationToken cancellationToken)
    {
        return await _names.ForAsync(jobs.Where(job => job.StoppedByUserId is not null)
                                         .Select(job => job.StoppedByUserId!.Value),
                                     cancellationToken);
    }

    /// <summary>
    /// How much one person has used each of a set of printers since a moment - the front page's
    /// "most used" ordering, counted rather than guessed at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Access comes from <paramref name="printerIds"/>, and there is no per-printer check here.</b>
    /// That is not an omission: this is a question across printers, so there is no single id to
    /// require a capability on, and answering "you have never used the one you cannot see" would
    /// still be answering about a printer somebody may not know exists. The caller passes the ids it
    /// was already granted - in practice the output of
    /// <see cref="PrinterQueryService.ListPrintersWithStateForUserAsync"/>, which is scoped to the
    /// caller - and this counts strictly inside that set. Widening it to every printer would leak the
    /// shape of the rack through a sort order.
    /// </para>
    /// <para>
    /// <b>Counted on <see cref="PrintJob.QueuedByUserId"/>, so "used" means you asked for it.</b> Not
    /// who stopped it, and not who happened to be signed in while it ran: the front page is answering
    /// "where do you send work", and the person who queued a job is the one who decided that.
    /// </para>
    /// <para>
    /// <b>Every job in the window counts, finished or not</b> - unlike <see cref="ListAsync(int, Caller, CancellationToken)"/>, which
    /// shows history and therefore wants <c>EndedAt</c> set. A print running right now is the
    /// strongest evidence there is that you use this printer, and excluding it would drop a printer
    /// down the page at the exact moment you were watching it work.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<int, PrinterUsage>> CountForUserAsync(
        long userId,
        IReadOnlyCollection<int> printerIds,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(printerIds);

        if (printerIds.Count == 0)
        {
            return new Dictionary<int, PrinterUsage>();
        }

        var counted = await _dbContext.PrintJobs
                                      .AsNoTracking()
                                      .Where(job => job.QueuedByUserId == userId &&
                                                    job.StartedAt >= since &&
                                                    printerIds.Contains(job.PrinterId))
                                      .GroupBy(job => job.PrinterId)
                                      .Select(group => new
                                      {
                                          PrinterId = group.Key,
                                          Jobs = group.Count(),
                                          LastStartedAt = group.Max(job => job.StartedAt),
                                      })
                                      .ToListAsync(cancellationToken);

        return counted.ToDictionary(row => row.PrinterId,
                                    row => new PrinterUsage(row.Jobs, row.LastStartedAt));
    }

    /// <summary>
    /// Why this printer's queue is held, as a sentence to be said rather than one already said.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from the <i>(file, printer)</i> row of whatever sits at the head, because that is where
    /// the loop records a block - <see cref="QueuedPrint"/> stays property-less. Only the head can
    /// hold a queue: the loop never looks past it, which is the spooler behaviour the design chose
    /// over skipping.
    /// </para>
    /// <para>
    /// <b>Returns a key rather than prose</b>, because the row records what happened and nothing
    /// else. The loop that wrote it had no reader and no culture; this has neither either, and the
    /// page that calls it has both. See <see cref="MessageKey"/>.
    /// </para>
    /// </remarks>
    public async Task<MessageKey?> GetHoldReasonAsync(int printerId, Caller caller, CancellationToken cancellationToken)
    {
        await _access.RequireAsync(printerId, caller, Capability.ViewQueue, cancellationToken);

        // The file comes with it because a compatibility hold has no (file, printer) row to read a
        // name from: it can stop a queue before anything has ever been sent to the printer.
        QueuedPrint? head = await _dbContext.QueuedPrints
                                            .AsNoTracking()
                                            .Include(queued => queued.PrintFile)
                                            .Where(queued => queued.PrinterId == printerId)
                                            .OrderBy(queued => queued.Position)
                                            .ThenBy(queued => queued.Id)
                                            .FirstOrDefaultAsync(cancellationToken);

        if (head is null)
        {
            return null;
        }

        var hold = await _dbContext.PrintFilesOnPrinters
                                   .AsNoTracking()
                                   .Where(row => row.PrinterId == printerId && row.PrintFileId == head.PrintFileId)
                                   .Select(row => new
                                   {
                                       row.HoldReason,
                                       row.HoldPrinterFreeBytes,
                                       row.HoldPrinterFileBytes,
                                       row.TransferRefusalCount,
                                       row.TransferRefusalCode,
                                       row.TransferRefusalReason,
                                       FileName = row.PrintFile!.Name,
                                       OurBytes = row.PrintFile!.Size,
                                   })
                                   .SingleOrDefaultAsync(cancellationToken);

        // The loop's own answer, so the banner cannot say "nothing is in the way" while the queue
        // refuses to move. The stored columns above still supply the numbers the space and name
        // sentences carry; the compatibility ones need only the file's name.
        PrintHoldReason? reason = (await _snapshots.ReadAsync(printerId, cancellationToken)).HoldReason;

        if (reason is PrintHoldReason.AbrasiveFilamentNeedsHardenedNozzle or
            PrintHoldReason.IncompatiblePrinterModel)
        {
            return MessageKey.For(reason == PrintHoldReason.AbrasiveFilamentNeedsHardenedNozzle ?
                                      "Queue_HoldAbrasiveFilament" :
                                      "Queue_HoldIncompatibleModel",
                                  hold?.FileName ?? head.PrintFile?.Name ?? string.Empty);
        }

        return hold?.HoldReason switch
        {
            PrintHoldReason.InsufficientSpace => MessageKey.For(
                "Queue_HoldInsufficientSpace", hold.FileName, hold.OurBytes, hold.HoldPrinterFreeBytes ?? 0),

            PrintHoldReason.FileExistsDifferentSize => MessageKey.For(
                "Queue_HoldFileExists", hold.FileName, hold.HoldPrinterFileBytes ?? 0, hold.OurBytes),

            PrintHoldReason.FileExistsUnknownSize => MessageKey.For(
                "Queue_HoldFileExistsUnknownSize", hold.FileName),

            // The one hold that names an uncertainty rather than a condition, so the sentence has to
            // say what was not established and leave the decision with the reader. It carries no
            // numbers: there are none to give.
            PrintHoldReason.PrintStartUnresolved => MessageKey.For(
                "Queue_HoldPrintStartUnresolved", hold.FileName),

            // The printer's words are an argument rather than part of the sentence, so they reach
            // the page as text to encode, and a reader sees them verbatim beside our explanation.
            // The code stands in when a printer sent no words.
            PrintHoldReason.TransferRefused => MessageKey.For(
                "Queue_HoldTransferRefused",
                hold.FileName,
                hold.TransferRefusalCount ?? 0,
                hold.TransferRefusalReason ?? hold.TransferRefusalCode ?? string.Empty),

            // Undefined is not a hold, and neither is null. Both answer "nothing is in the way"
            // rather than inventing a sentence for a value nothing writes.
            _ => null,
        };
    }
}
