using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Exceptions;
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

    private readonly HSDbContext _dbContext;
    private readonly TeamService _teamService;

    public PrintHistoryService(HSDbContext dbContext, TeamService teamService)
    {
        _dbContext = dbContext;
        _teamService = teamService;
    }

    /// <summary>
    /// The print running on this printer, or null when none is.
    /// </summary>
    /// <remarks>
    /// "Running" here means <see cref="PrintJob.EndedAt"/> is null, which includes the seconds a print
    /// spends <see cref="Model.PrintOutcome.Starting"/> before the printer reports itself printing.
    /// A page showing this should say so rather than claiming the machine is printing already.
    /// </remarks>
    public async Task<PrintJob?> GetActiveAsync(int printerId, long userId, CancellationToken cancellationToken)
    {
        await AuthoriseAsync(printerId, userId, cancellationToken);

        return await _dbContext.PrintJobs
                               .AsNoTracking()
                               .SingleOrDefaultAsync(job => job.PrinterId == printerId && job.EndedAt == null,
                                   cancellationToken);
    }

    /// <summary>Finished prints, newest first.</summary>
    public async Task<IReadOnlyList<PrintJob>> ListAsync(int printerId, long userId,
        CancellationToken cancellationToken)
    {
        await AuthoriseAsync(printerId, userId, cancellationToken);

        return await _dbContext.PrintJobs
                               .AsNoTracking()
                               .Where(job => job.PrinterId == printerId && job.EndedAt != null)
                               .OrderByDescending(job => job.StartedAt)
                               .ThenByDescending(job => job.Id)
                               .Take(RecentCount)
                               .ToListAsync(cancellationToken);
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
        await AuthoriseAsync(printerId, userId, cancellationToken);

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

    private async Task AuthoriseAsync(int printerId, long userId, CancellationToken cancellationToken)
    {
        Printer? printer = await _dbContext.Printers
                                           .AsNoTracking()
                                           .SingleOrDefaultAsync(candidate => candidate.Id == printerId,
                                               cancellationToken);

        if (printer is null)
        {
            throw PrinterNotFoundException.ForId(printerId);
        }

        TeamMember? membership = await _teamService.GetMemberAsync(printer.TeamId, userId, cancellationToken);

        if (membership is null || !membership.CanRead)
        {
            throw new TeamAccessDeniedException();
        }
    }
}
