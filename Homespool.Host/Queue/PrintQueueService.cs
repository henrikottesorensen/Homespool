using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Queue;

/// <summary>
/// A printer's queue: what is waiting, and the three things a person can do to it - add, reorder,
/// cancel.
/// </summary>
/// <remarks>
/// <para>
/// <b>One queue per printer, shared by everyone who may use it</b> (Henrik,
/// <c>notes/print-queue.md</c>): if you may use the printer, you may manipulate its queue - reorder
/// it, cancel from it, including entries somebody else added. That is how a shared printer is
/// actually used, and it needs no permission this app does not already have. A queue per person
/// would have to answer whose turn it is, which is a question nobody asked.
/// </para>
/// <para>
/// <b>Reading is <c>CanRead</c>, changing is <c>CanUse</c>.</b> Seeing what a printer will do next is
/// the same class of thing as seeing its temperature. Unlike
/// <c>PrinterController.Storage</c>, none of this makes the printer go and work - it is all database.
/// </para>
/// <para>
/// <b>Nothing here advances the queue.</b> This is the list; the printer's producer loop is what pulls
/// from it, and that is a separate piece with its own reasons for existing where it does. So there is
/// no notion here of a job being "current" or "transferring" - a row is waiting or it is gone.
/// </para>
/// </remarks>
public class PrintQueueService
{
    private readonly HSDbContext _dbContext;
    private readonly TeamService _teamService;
    private readonly PrintFileCatalog _files;
    private readonly TimeProvider _timeProvider;
    private readonly QueueSignal _signal;

    public PrintQueueService(HSDbContext dbContext,
                             TeamService teamService,
                             PrintFileCatalog files,
                             TimeProvider timeProvider,
                             QueueSignal signal)
    {
        _dbContext = dbContext;
        _teamService = teamService;
        _files = files;
        _timeProvider = timeProvider;
        _signal = signal;
    }

    /// <summary>
    /// What <paramref name="printerId"/> will print, in the order it will print it.
    /// </summary>
    /// <exception cref="PrinterNotFoundException">No printer has that id.</exception>
    /// <exception cref="TeamAccessDeniedException">Caller lacks <c>CanRead</c> on the printer's team.</exception>
    public async Task<IReadOnlyList<QueuedPrint>> ListAsync(int printerId,
                                                            long userId,
                                                            CancellationToken cancellationToken)
    {
        await AuthoriseAsync(printerId, userId, requireUse: false, cancellationToken);

        return await _dbContext.QueuedPrints
                               .AsNoTracking()
                               .Include(job => job.PrintFile)
                               .Where(job => job.PrinterId == printerId)
                               .OrderBy(job => job.Position)
                               .ThenBy(job => job.Id)
                               .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Adds one of <paramref name="userId"/>'s files to the end of a printer's queue.
    /// </summary>
    /// <exception cref="PrinterNotFoundException">No printer has that id.</exception>
    /// <exception cref="TeamAccessDeniedException">Caller lacks <c>CanUse</c> on the printer's team.</exception>
    /// <exception cref="PrintFileNotFoundException">The caller has no file by that name.</exception>
    /// <remarks>
    /// <b>The same file may be queued more than once</b>, deliberately - printing two copies is an
    /// ordinary thing to want, and the loop transfers the bytes once regardless because the transfer
    /// belongs to <i>(file, printer)</i> rather than to the entry.
    /// </remarks>
    public async Task<QueuedPrint> EnqueueAsync(int printerId,
                                                long userId,
                                                string fileName,
                                                CancellationToken cancellationToken)
    {
        await AuthoriseAsync(printerId, userId, requireUse: true, cancellationToken);

        PrintFile? file = await _files.ResolveAsync(userId, fileName, cancellationToken);

        if (file is null)
        {
            throw new PrintFileNotFoundException(fileName);
        }

        // Max rather than Count, so cancelling from the middle cannot make a later enqueue collide
        // with a position already in use. Gaps are harmless - the order is the sort, not the values.
        int? last = await _dbContext.QueuedPrints
                                    .Where(job => job.PrinterId == printerId)
                                    .MaxAsync(job => (int?)job.Position, cancellationToken);

        QueuedPrint queued = new()
        {
            PrinterId = printerId,
            PrintFileId = file.Id,
            Position = (last ?? -1) + 1,
            QueuedByUserId = userId,
            QueuedAt = _timeProvider.GetUtcNow(),
        };

        _dbContext.QueuedPrints.Add(queued);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // After the save, so the loop cannot wake and read a queue this row is not in yet. Somebody
        // pressed Queue and is watching; the poke is what makes it happen now rather than within a
        // poll interval.
        _signal.Poke();

        return queued;
    }

    /// <summary>
    /// Moves a queued print to <paramref name="targetIndex"/>, counting from zero, and renumbers the
    /// queue around it. An index outside the queue is clamped to its ends.
    /// </summary>
    /// <exception cref="TeamAccessDeniedException">Caller lacks <c>CanUse</c> on the printer's team.</exception>
    /// <remarks>
    /// <para>
    /// Renumbering the whole queue rather than swapping two rows: at a depth measured in single digits
    /// that is a handful of updates in one <c>SaveChangesAsync</c> - one round trip, so no transaction
    /// of its own (<c>notes/transactions.md</c>) - and it leaves the positions describing the order
    /// plainly rather than as an arithmetic puzzle.
    /// </para>
    /// <para>
    /// <b>Reordering past a file already sent to the printer is allowed</b> and costs nothing: the
    /// bytes simply sit on the drive unused. That is the pipelining trade
    /// (<c>notes/print-queue.md</c>), and the printer's storage listing is what can find them again.
    /// </para>
    /// </remarks>
    /// <returns>False if there is no such queued print.</returns>
    public async Task<bool> MoveAsync(long queuedPrintId,
                                      long userId,
                                      int targetIndex,
                                      CancellationToken cancellationToken)
    {
        QueuedPrint? job = await _dbContext.QueuedPrints
                                         .SingleOrDefaultAsync(candidate => candidate.Id == queuedPrintId,
                                             cancellationToken);

        if (job is null)
        {
            return false;
        }

        await AuthoriseAsync(job.PrinterId, userId, requireUse: true, cancellationToken);

        List<QueuedPrint> queue = await _dbContext.QueuedPrints
                                                .Where(candidate => candidate.PrinterId == job.PrinterId)
                                                .OrderBy(candidate => candidate.Position)
                                                .ThenBy(candidate => candidate.Id)
                                                .ToListAsync(cancellationToken);

        queue.Remove(queue.Single(candidate => candidate.Id == job.Id));
        queue.Insert(Math.Clamp(targetIndex, 0, queue.Count), job);

        for (int position = 0; position < queue.Count; position++)
        {
            queue[position].Position = position;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// Removes a queued print. Cancelling <i>is</i> deleting the row - there is no cancelled state,
    /// because a queue entry records an intention and the intention is gone.
    /// </summary>
    /// <exception cref="TeamAccessDeniedException">Caller lacks <c>CanUse</c> on the printer's team.</exception>
    /// <remarks>
    /// <b>This never stops a print.</b> A job the loop has already started is a <c>Job</c>, not a queue
    /// entry, and stopping it is a separate deliberate act - "don't cancel prints on people" (Henrik).
    /// </remarks>
    /// <returns>False if there is no such queued print.</returns>
    public async Task<bool> CancelAsync(long queuedPrintId, long userId, CancellationToken cancellationToken)
    {
        QueuedPrint? job = await _dbContext.QueuedPrints
                                         .SingleOrDefaultAsync(candidate => candidate.Id == queuedPrintId,
                                             cancellationToken);

        if (job is null)
        {
            return false;
        }

        await AuthoriseAsync(job.PrinterId, userId, requireUse: true, cancellationToken);

        _dbContext.QueuedPrints.Remove(job);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// The one place this service decides whether a caller may touch a printer's queue.
    /// </summary>
    /// <remarks>
    /// Reads the printer to find its team, exactly as <c>PrinterCommandService</c> does - permission
    /// belongs to the printer's team rather than to the queue, so there is nothing new to grant.
    /// </remarks>
    private async Task AuthoriseAsync(int printerId,
                                      long userId,
                                      bool requireUse,
                                      CancellationToken cancellationToken)
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

        if (membership is null || (requireUse ? !membership.CanUse : !membership.CanRead))
        {
            throw new TeamAccessDeniedException();
        }
    }
}
