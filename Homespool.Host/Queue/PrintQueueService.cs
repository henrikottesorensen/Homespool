using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Queue;

/// <summary>
/// A printer's queue: what is waiting, and the three things a person can do to it - add, reorder,
/// cancel.
/// </summary>
/// <remarks>
/// <para>
/// <b>One queue per printer, shared by everyone who may use it</b>: if you may use the printer,
/// you may manipulate its queue - reorder
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
    private readonly HomespoolDbContext _dbContext;
    private readonly PrinterAccessService _access;
    private readonly PrintFileCatalog _files;
    private readonly TimeProvider _timeProvider;
    private readonly QueueSignal _signal;

    public PrintQueueService(HomespoolDbContext dbContext,
                             PrinterAccessService access,
                             PrintFileCatalog files,
                             TimeProvider timeProvider,
                             QueueSignal signal)
    {
        _dbContext = dbContext;
        _access = access;
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
                                                            Caller caller,
                                                            CancellationToken cancellationToken)
    {
        await _access.RequireAsync(printerId, caller, Capability.ViewQueue, cancellationToken);

        return await _dbContext.QueuedPrints
                               .AsNoTracking()
                               .Include(job => job.PrintFile)
                               .Where(job => job.PrinterId == printerId)
                               .OrderBy(job => job.Position)
                               .ThenBy(job => job.Id)
                               .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// How many files are queued on each of a set of printers - the front page's "3 queued", counted
    /// in one query rather than by listing every printer's queue in turn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Access comes from <paramref name="printerIds"/>, and there is no per-printer check.</b>
    /// The same shape as <see cref="Services.PrintHistoryService.CountForUserAsync"/> and for the same
    /// reason: a question spanning printers has no single id to require a capability on. The caller
    /// passes the ids it was already granted and this counts strictly inside that set.
    /// </para>
    /// <para>
    /// <b>Every row counts, because every row is a wait.</b> A queued print leaves this table when it
    /// becomes a <see cref="PrintJob"/>, so what is left is exactly what has not started - including
    /// an entry held on a condition somebody has to clear. Filtering those out would quietly under-
    /// report the queue that most needs attention.
    /// </para>
    /// <para>
    /// Printers with an empty queue are absent from the result rather than present with a zero. The
    /// page renders nothing for them, so absence and zero mean the same thing and the dictionary
    /// stays the size of what it has to say.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<int, int>> CountByPrinterAsync(
        IReadOnlyCollection<int> printerIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(printerIds);

        if (printerIds.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        var counted = await _dbContext.QueuedPrints
                                      .AsNoTracking()
                                      .Where(job => printerIds.Contains(job.PrinterId))
                                      .GroupBy(job => job.PrinterId)
                                      .Select(group => new { PrinterId = group.Key, Waiting = group.Count() })
                                      .ToListAsync(cancellationToken);

        return counted.ToDictionary(row => row.PrinterId, row => row.Waiting);
    }

    /// <summary>
    /// Adds one of the caller's files to the end of a printer's queue.
    /// </summary>
    /// <exception cref="PrinterNotFoundException">No printer has that id.</exception>
    /// <exception cref="TeamAccessDeniedException">Caller lacks <c>CanUse</c> on the printer's team.</exception>
    /// <exception cref="PrintFileNotFoundException">The caller has no file by that name.</exception>
    /// <remarks>
    /// <b>The same file may be queued more than once</b>, deliberately - printing two copies is an
    /// ordinary thing to want, and the loop transfers the bytes once regardless because the transfer
    /// belongs to <i>(file, printer)</i> rather than to the entry.
    /// </remarks>
    public async Task<EnqueueOutcome> EnqueueAsync(int printerId,
                                                   Caller caller,
                                                   string fileName,
                                                   CancellationToken cancellationToken)
    {
        await _access.RequireAsync(printerId, caller, Capability.Print, cancellationToken);

        PrintFile? file = await _files.ResolveAsync(caller.UserId, fileName, cancellationToken);

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

            // The caller's handle for the whole lifecycle - minted here because enqueue is where the
            // intention begins, and everything that becomes of it carries this forward.
            TrackingId = Guid.NewGuid(),
            Position = (last ?? -1) + 1,
            QueuedByUserId = caller.UserId,
            QueuedByScope = caller.ScopeToRecord,
            QueuedAt = _timeProvider.GetUtcNow(),
        };

        _dbContext.QueuedPrints.Add(queued);

        // Queueing a file again is the deliberate act that answers an unresolved print start
        // (PrintHoldReason.PrintStartUnresolved): the loop could not establish whether a previous
        // START_PRINT of this file took, and asking for it now is somebody saying they have looked.
        // Scoped to that one reason - the other holds are conditions on the printer, and none of
        // them is cleared by wanting the file more.
        PrintFileOnPrinter? unresolved = await _dbContext.PrintFilesOnPrinters
                                                         .SingleOrDefaultAsync(
                                                             row => row.PrinterId == printerId
                                                                    && row.PrintFileId == file.Id
                                                                    && row.HoldReason == PrintHoldReason.PrintStartUnresolved,
                                                             cancellationToken);

        if (unresolved is not null)
        {
            unresolved.HoldReason = null;
            unresolved.BlockedAt = null;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // After the save, so the loop cannot wake and read a queue this row is not in yet. Somebody
        // pressed Queue and is watching; the poke is what makes it happen now rather than within a
        // poll interval.
        _signal.Poke();

        return await OutcomeAsync(printerId, file, queued, cancellationToken);
    }

    /// <summary>
    /// How this file and this printer disagree, for telling whoever just queued it.
    /// </summary>
    /// <remarks>
    /// <b>Read after the entry is saved, not before.</b> Nothing here can refuse the enqueue, so
    /// there is no reason to make somebody wait on two more queries before their entry exists - and
    /// doing it afterwards means a printer that has never reported its hardware costs one empty
    /// lookup rather than blocking the common path.
    /// </remarks>
    private async Task<EnqueueOutcome> OutcomeAsync(int printerId,
                                                    PrintFile file,
                                                    QueuedPrint queued,
                                                    CancellationToken cancellationToken)
    {
        Printer? printer = await _dbContext.Printers
                                           .AsNoTracking()
                                           .SingleOrDefaultAsync(row => row.Id == printerId, cancellationToken);

        if (printer is null)
        {
            return new EnqueueOutcome(queued, [], []);
        }

        List<PrinterTool> tools = await _dbContext.PrinterTools
                                                  .AsNoTracking()
                                                  .Where(tool => tool.PrinterId == printerId)
                                                  .ToListAsync(cancellationToken);

        IReadOnlyList<PrintCompatibilityFinding> findings = PrintFileCompatibility.Evaluate(file, printer, tools);

        return new EnqueueOutcome(
            queued,
            findings,
            [.. findings.Select(finding => PrintCompatibilityDescription.For(finding, file, printer, tools))]);
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
    /// of its own - and it leaves the positions describing the order
    /// plainly rather than as an arithmetic puzzle.
    /// </para>
    /// <para>
    /// <b>Reordering past a file already sent to the printer is allowed</b> and costs nothing: the
    /// bytes simply sit on the drive unused. That is the pipelining trade, and the printer's
    /// storage listing is what can find them again.
    /// </para>
    /// <para>
    /// <b>By <see cref="QueuedPrint.TrackingId"/>, not the primary key</b> - the handle is the only
    /// identifier a caller ever holds, exactly as printers are reached by <c>Printer.Uuid</c> and
    /// never by <c>Printer.Id</c>. <see cref="CancelAsync"/> resolves the same way.
    /// </para>
    /// </remarks>
    /// <returns>False if there is no such queued print.</returns>
    public async Task<bool> MoveAsync(Guid trackingId,
                                      Caller caller,
                                      int targetIndex,
                                      CancellationToken cancellationToken)
    {
        QueuedPrint? job = await _dbContext.QueuedPrints
                                           .SingleOrDefaultAsync(candidate => candidate.TrackingId == trackingId,
                                                                 cancellationToken);

        if (job is null)
        {
            return false;
        }

        // Reordering moves other people's work as well as your own - there is one queue - so it is
        // the same right as withdrawing somebody else's, not the same right as adding your own.
        await _access.RequireAsync(job.PrinterId, caller, Capability.ControlPrinter, cancellationToken);

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
    /// <exception cref="TeamAccessDeniedException">
    /// The caller may not withdraw this entry - see the remarks.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>This never stops a print.</b> A job the loop has already started is a <c>Job</c>, not a queue
    /// entry, and stopping it is a separate deliberate act - "don't cancel prints on people" (Henrik).
    /// </para>
    /// <para>
    /// <b>Whose entry it is decides who may remove it.</b> <see cref="Capability.Print"/> withdraws
    /// your own, <see cref="Capability.ControlPrinter"/> withdraws anybody's. That narrows the older
    /// rule that anyone able to use the printer could cancel anyone's entry - which was written when
    /// the vocabulary could not tell the two apart. "The queue is the printer's, not the queuer's"
    /// still holds, for whoever holds <see cref="Capability.ControlPrinter"/>.
    /// </para>
    /// </remarks>
    /// <returns>False if there is no such queued print.</returns>
    public async Task<bool> CancelAsync(Guid trackingId, Caller caller, CancellationToken cancellationToken)
    {
        QueuedPrint? job = await _dbContext.QueuedPrints
                                           .SingleOrDefaultAsync(candidate => candidate.TrackingId == trackingId,
                                                                 cancellationToken);

        if (job is null)
        {
            return false;
        }

        await _access.RequireWithdrawingAsync(job.PrinterId, caller, job.QueuedByUserId, cancellationToken);

        _dbContext.QueuedPrints.Remove(job);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }
}
