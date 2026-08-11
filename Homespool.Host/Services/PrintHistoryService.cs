using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Model.Entities;

namespace Homespool.Host.Services;

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

    public PrintHistoryService(HomespoolDbContext dbContext, PrinterAccessService access)
    {
        _dbContext = dbContext;
        _access = access;
    }

    /// <summary>
    /// The print running on this printer, or null when none is.
    /// </summary>
    /// <remarks>
    /// "Running" here means <see cref="PrintJob.EndedAt"/> is null, which includes the seconds a print
    /// spends <see cref="Model.PrintState.Starting"/> before the printer reports itself printing.
    /// A page showing this should say so rather than claiming the machine is printing already.
    /// </remarks>
    public async Task<PrintJob?> GetActiveAsync(int printerId, long userId, CancellationToken cancellationToken)
    {
        await _access.RequireAsync(printerId, userId, PrinterOperation.ViewHistory, cancellationToken);

        return await _dbContext.PrintJobs
                               .AsNoTracking()
                               .SingleOrDefaultAsync(job => job.PrinterId == printerId && job.EndedAt == null,
                                                     cancellationToken);
    }

    /// <summary>Finished prints, newest first.</summary>
    public async Task<IReadOnlyList<PrintJob>> ListAsync(int printerId,
                                                         long userId,
                                                         CancellationToken cancellationToken)
    {
        await _access.RequireAsync(printerId, userId, PrinterOperation.ViewHistory, cancellationToken);

        return await _dbContext.PrintJobs
                               .AsNoTracking()
                               .Where(job => job.PrinterId == printerId && job.EndedAt != null)
                               .OrderByDescending(job => job.StartedAt)
                               .ThenByDescending(job => job.Id)
                               .Take(RecentCount)
                               .ToListAsync(cancellationToken);
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
    /// <see cref="PrintJob"/> at all has been through <see cref="ListAsync"/> or
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
        long[] ids = jobs.Where(job => job.StoppedByUserId is not null)
                         .Select(job => job.StoppedByUserId!.Value)
                         .Distinct()
                         .ToArray();

        if (ids.Length == 0)
        {
            return new Dictionary<long, string>();
        }

        return await _dbContext.Users
                               .AsNoTracking()
                               .Where(user => ids.Contains(user.Id) && user.UserName != null)
                               .ToDictionaryAsync(user => user.Id, user => user.UserName!, cancellationToken);
    }

    /// <summary>
    /// Why this printer's queue is held, or null when nothing is in the way.
    /// </summary>
    /// <remarks>
    /// Read from the <i>(file, printer)</i> row of whatever sits at the head, because that is where
    /// the loop records a block - <see cref="QueuedPrint"/> stays property-less. Only the head can
    /// hold a queue: the loop never looks past it, which is the spooler behaviour the design chose
    /// over skipping.
    /// </remarks>
    public async Task<string?> GetHoldReasonAsync(int printerId, long userId, CancellationToken cancellationToken)
    {
        await _access.RequireAsync(printerId, userId, PrinterOperation.ViewQueue, cancellationToken);

        QueuedPrint? head = await _dbContext.QueuedPrints
                                            .AsNoTracking()
                                            .Where(queued => queued.PrinterId == printerId)
                                            .OrderBy(queued => queued.Position)
                                            .ThenBy(queued => queued.Id)
                                            .FirstOrDefaultAsync(cancellationToken);

        if (head is null)
        {
            return null;
        }

        return await _dbContext.PrintFilesOnPrinters
                               .AsNoTracking()
                               .Where(row => row.PrinterId == printerId && row.PrintFileId == head.PrintFileId)
                               .Select(row => row.BlockedReason)
                               .SingleOrDefaultAsync(cancellationToken);
    }
}
