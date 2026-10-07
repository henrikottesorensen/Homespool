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
using Homespool.Host.Printing;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Queue;

/// <summary>
/// A printer's queue: what is waiting, and the three things a person can do to it - add, reorder,
/// cancel.
/// </summary>
/// <remarks>
/// <para>
/// <b>One queue per printer, shared by everyone who may print on it.</b> A queue per person would
/// have to answer whose turn it is, which is a question nobody asked.
/// </para>
/// <para>
/// <b>Reading is <see cref="Capability.ViewQueue"/>; adding, and withdrawing your own entry, is
/// <see cref="Capability.Print"/>; reordering, or withdrawing somebody else's, is
/// <see cref="Capability.ControlPrinter"/>.</b> Seeing what a printer will do next is the same class
/// of thing as seeing its temperature. Unlike
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
    private readonly PrintHistoryService _history;
    private readonly TeamCapabilityLookup _teams;

    public PrintQueueService(HomespoolDbContext dbContext,
                             PrinterAccessService access,
                             PrintFileCatalog files,
                             TimeProvider timeProvider,
                             QueueSignal signal,
                             PrintHistoryService history,
                             TeamCapabilityLookup teams)
    {
        _dbContext = dbContext;
        _access = access;
        _teams = teams;
        _files = files;
        _timeProvider = timeProvider;
        _signal = signal;
        _history = history;
    }

    /// <summary>
    /// What <paramref name="printerId"/> will print, in the order it will print it.
    /// </summary>
    /// <exception cref="PrinterNotFoundException">No printer has that id.</exception>
    /// <exception cref="TeamAccessDeniedException">Caller lacks <see cref="Capability.ViewQueue"/> on the printer's team.</exception>
    public async Task<IReadOnlyList<QueuedPrint>> ListAsync(int printerId,
                                                            Caller caller,
                                                            CancellationToken cancellationToken)
    {
        await _access.RequireAsync(printerId, caller, Capability.ViewQueue, cancellationToken);

        return await _dbContext.QueuedPrints
                               .AsNoTracking()
                               .Include(job => job.File)
                               .Where(job => job.PrinterId == printerId)
                               .OrderBy(job => job.Position)
                               .ThenBy(job => job.Id)
                               .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// How many files are queued on each of a set of printers - the front page's "3 queued", counted
    /// in one query rather than by listing every printer's queue in turn.
    /// </summary>
    /// <param name="printerIds">The printers to count, already resolved for the caller.</param>
    /// <param name="caller">Who is asking; only printers whose team lets them see the queue are counted.</param>
    /// <param name="cancellationToken">The request's own.</param>
    /// <remarks>
    /// <para>
    /// <b>Counted only where the caller holds <see cref="Capability.ViewQueue"/></b>, from one read of
    /// their memberships rather than a check per printer. The ids come from a listing that asked
    /// <see cref="Capability.ViewPrinter"/>, which is not the same question: a queue's depth is the
    /// queue's, and a printer the caller may see but whose queue they may not is absent from the
    /// result - read as an empty queue, which is what such a reader is shown.
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
        Caller caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(printerIds);

        if (printerIds.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        IReadOnlyCollection<int> teams = await _teams.TeamsAllowingAsync(caller, Capability.ViewQueue, cancellationToken);

        if (teams.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        var counted = await _dbContext.QueuedPrints
                                      .AsNoTracking()
                                      .Where(job => printerIds.Contains(job.PrinterId))
                                      .Where(job => _dbContext.Printers.Any(printer => printer.Id == job.PrinterId &&
                                                                                       teams.Contains(printer.TeamId)))
                                      .GroupBy(job => job.PrinterId)
                                      .Select(group => new { PrinterId = group.Key, Waiting = group.Count() })
                                      .ToListAsync(cancellationToken);

        return counted.ToDictionary(row => row.PrinterId, row => row.Waiting);
    }

    /// <summary>
    /// Adds one of the caller's files to the end of a printer's queue.
    /// </summary>
    /// <exception cref="PrinterNotFoundException">No printer has that id.</exception>
    /// <exception cref="TeamAccessDeniedException">Caller lacks <see cref="Capability.Print"/> on the printer's team.</exception>
    /// <exception cref="PrintFileNotFoundException">The caller has no file by that name.</exception>
    /// <exception cref="IncompatiblePrinterModelException">
    /// The file was sliced for a machine this printer must not be asked to imitate.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>The same file may be queued more than once</b>, deliberately - printing two copies is an
    /// ordinary thing to want, and the loop transfers the bytes once regardless because the transfer
    /// belongs to <i>(file, printer)</i> rather than to the entry.
    /// </para>
    /// <para>
    /// <b>One disagreement refuses and the rest are warnings</b>, which is a split about what can be
    /// cleared rather than about what is serious. A soft nozzle can be swapped for a hardened one and
    /// the waiting entry then runs, so keeping it is worth something; a printer cannot be swapped for
    /// a different model, so an entry queued against that would wait on nothing while the queue behind
    /// it waited too. <see cref="PrintFileCompatibility"/> still decides what is wrong - this decides
    /// only what to do about it.
    /// </para>
    /// </remarks>
    public async Task<EnqueueOutcome> EnqueueAsync(int printerId,
                                                   Caller caller,
                                                   string fileName,
                                                   CancellationToken cancellationToken)
    {
        await _access.RequireAsync(printerId, caller, Capability.Print, cancellationToken);

        HSFile? file = await _files.ResolveAsync(caller.UserId, fileName, cancellationToken);

        if (file is null)
        {
            throw new PrintFileNotFoundException(fileName);
        }

        // Before the row is written, which is what refusing at all requires: a print this machine
        // must not be asked for has to be turned away while there is still nothing to withdraw. The
        // two reads it needs are the ones the outcome below wanted anyway, moved rather than added.
        (Printer? printer, List<PrinterTool> tools) = await HardwareAsync(printerId, cancellationToken);

        IReadOnlyList<PrintCompatibilityFinding> findings =
            printer is null ? [] : PrintFileCompatibility.Evaluate(file, printer, tools);

        if (findings.Contains(PrintCompatibilityFinding.IncompatiblePrinterModel))
        {
            throw new IncompatiblePrinterModelException(file.Name,
                                                        file.PrinterModel,
                                                        PrinterModelDesignation.ForDisplay(printer!.Model));
        }

        // Max rather than Count, so cancelling from the middle cannot make a later enqueue collide
        // with a position already in use. Gaps are harmless - the order is the sort, not the values.
        int? last = await _dbContext.QueuedPrints
                                    .Where(job => job.PrinterId == printerId)
                                    .MaxAsync(job => (int?)job.Position, cancellationToken);

        QueuedPrint queued = new()
        {
            PrinterId = printerId,
            FileId = file.Id,

            // The caller's handle for the whole lifecycle - minted here because enqueue is where the
            // intention begins, and everything that becomes of it carries this forward.
            PrintUuid = Guid.NewGuid(),
            Position = (last ?? -1) + 1,
            QueuedByUserId = caller.UserId,
            QueuedByScope = caller.ScopeToRecord,
            QueuedAt = _timeProvider.GetUtcNow(),
        };

        _dbContext.QueuedPrints.Add(queued);

        // Queueing a file again is the deliberate act that answers the holds whose exit is a person:
        // an unresolved print start (PrintHoldReason.PrintStartUnresolved), where the loop could not
        // establish whether a previous START_PRINT took; a transfer the printer kept refusing or kept
        // abandoning (TransferRefused, TransferAborted), where waiting has already been tried; and a
        // transfer somebody stopped at the printer (TransferStopped); and a file too large to send
        // (FileTooLarge), which a smaller one under the same name answers. Asking for the file now is
        // somebody saying they have looked. Scoped to those - the other holds are conditions on the
        // printer the loop re-checks itself, and none of them is cleared by wanting the file more.
        FileOnPrinter? personHeld = await _dbContext.FilesOnPrinters
                                                    .SingleOrDefaultAsync(
                                                        row => row.PrinterId == printerId &&
                                                               row.FileId == file.Id &&
                                                               (row.HoldReason == PrintHoldReason.PrintStartUnresolved ||
                                                                row.HoldReason == PrintHoldReason.TransferRefused ||
                                                                row.HoldReason == PrintHoldReason.TransferAborted ||
                                                                row.HoldReason == PrintHoldReason.TransferStopped ||
                                                                row.HoldReason == PrintHoldReason.FileTooLarge),
                                                        cancellationToken);

        if (personHeld is not null)
        {
            personHeld.HoldReason = null;
            personHeld.BlockedAt = null;

            // A fresh count as well as a lifted hold, or the next identical refusal would re-hold at
            // once and the re-queue would buy a single attempt.
            TransferRetryRules.Forget(personHeld);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // After the save, so the loop cannot wake and read a queue this row is not in yet. Somebody
        // pressed Queue and is watching; the poke is what makes it happen now rather than within a
        // poll interval.
        _signal.Poke();

        // What is left is what a person is told rather than what stops them: the warnings, and the
        // one hold that a person can go and clear by fitting a different nozzle.
        return printer is null ?
            new EnqueueOutcome(queued, file, [], []) :
            new EnqueueOutcome(
                queued,
                file,
                findings,
                [.. findings.Select(finding => PrintCompatibilityDescription.For(finding, file, printer, tools))]);
    }

    /// <summary>
    /// Queues one of the caller's own prints again, by the handle it was queued under.
    /// </summary>
    /// <exception cref="TeamAccessDeniedException">
    /// Caller lacks <see cref="Capability.ViewHistory"/> or <see cref="Capability.Print"/> on the
    /// printer's team.
    /// </exception>
    /// <exception cref="PrintNotYoursException">Somebody else queued that print.</exception>
    /// <exception cref="PrintFileNotFoundException">The caller no longer has a file by that name.</exception>
    /// <exception cref="PrintFileChangedException">
    /// The file under that name is not the bytes that printed, and <paramref name="acceptChanged"/> is false.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Only the person who queued a print may print it again, and that is a correctness rule
    /// rather than a permission one.</b> <see cref="PrintJob.FileName"/> is a record of what ran, not
    /// a pointer at it, so the file is resolved by name among the caller's own - and on somebody
    /// else's row that would print <i>your</i> file of that name while you believed you were
    /// repeating theirs. Nothing fails; the wrong thing prints.
    /// </para>
    /// <para>
    /// <b>The name finds the file; the digest says whether it is still the one that printed.</b> An
    /// overwrite since then leaves the name and changes the bytes, so a reprint would print something
    /// the person never saw print. That is refused unless <paramref name="acceptChanged"/> says the
    /// current version is wanted - asked before queueing, because the queue starts a print on a ready
    /// printer within seconds and a warning afterwards would come too late. Only compared when both
    /// digests are known: files from before digests were taken, or indexed from the disk, have none,
    /// and there is nothing to compare them with.
    /// </para>
    /// <para>
    /// <b>It queues rather than prints</b>, so a reprint takes the same route as every other way a file
    /// reaches a printer, and the new entry carries a handle of its own.
    /// </para>
    /// </remarks>
    /// <param name="printerId">The printer the print ran on.</param>
    /// <param name="printUuid">The handle the print was queued under.</param>
    /// <param name="caller">Who is asking.</param>
    /// <param name="acceptChanged">Queue the file as it is now, even if it has changed since that print.</param>
    /// <param name="cancellationToken">Aborted with the request.</param>
    /// <returns>Null if this printer has no print under that handle.</returns>
    public async Task<EnqueueOutcome?> ReprintAsync(int printerId,
                                                    Guid printUuid,
                                                    Caller caller,
                                                    bool acceptChanged,
                                                    CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);

        PrintJob? job = await _history.FindAsync(printerId, printUuid, caller, cancellationToken);

        if (job is null)
        {
            return null;
        }

        // Before the two refusals below, which are about the job rather than the caller: a key that
        // cannot print is told so, not that the print is somebody else's or that the file changed.
        await _access.RequireAsync(printerId, caller, Capability.Print, cancellationToken);

        if (job.QueuedByUserId != caller.UserId)
        {
            throw new PrintNotYoursException();
        }

        if (!acceptChanged && job.Digest is not null)
        {
            HSFile? current = await _files.ResolveAsync(caller.UserId, job.FileName, cancellationToken);

            // A missing file falls through to the enqueue, which says so in its own words.
            if (current?.Digest is not null && !string.Equals(current.Digest, job.Digest, StringComparison.Ordinal))
            {
                throw new PrintFileChangedException(job.FileName);
            }
        }

        return await EnqueueAsync(printerId, caller, job.FileName, cancellationToken);
    }

    /// <summary>
    /// The printer and the per-tool hardware a compatibility comparison needs, or a null printer when
    /// there is no such row - which silences the comparison rather than failing it.
    /// </summary>
    private async Task<(Printer? printer, List<PrinterTool> tools)> HardwareAsync(int printerId,
                                                                                  CancellationToken cancellationToken)
    {
        Printer? printer = await _dbContext.Printers
                                           .AsNoTracking()
                                           .SingleOrDefaultAsync(row => row.Id == printerId, cancellationToken);

        if (printer is null)
        {
            return (null, []);
        }

        List<PrinterTool> tools = await _dbContext.PrinterTools
                                                  .AsNoTracking()
                                                  .Where(tool => tool.PrinterId == printerId)
                                                  .ToListAsync(cancellationToken);

        return (printer, tools);
    }

    /// <summary>
    /// Moves a queued print to <paramref name="targetIndex"/>, counting from zero, and renumbers the
    /// queue around it. An index outside the queue is clamped to its ends.
    /// </summary>
    /// <exception cref="TeamAccessDeniedException">Caller lacks <see cref="Capability.ControlPrinter"/> on the printer's team.</exception>
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
    /// <b>By <see cref="QueuedPrint.PrintUuid"/>, not the primary key</b> - the handle is the only
    /// identifier a caller ever holds, exactly as printers are reached by <c>Printer.Uuid</c> and
    /// never by <c>Printer.Id</c>. <see cref="CancelAsync"/> resolves the same way.
    /// </para>
    /// <para>
    /// <b>Within <paramref name="printerId"/> only.</b> The handle is unique on its own, but the caller
    /// named a printer to get here, and an entry queued on a different one is not found rather than
    /// moved - otherwise the request would change one printer's queue under a URL naming another.
    /// <see cref="CancelAsync"/> is scoped the same way.
    /// </para>
    /// <para>
    /// <b>Refused before the entry is looked up</b>, so a caller who may not reorder this queue gets
    /// the same answer whether or not the handle names an entry - a refusal for one and
    /// <see langword="false"/> for the other would say which handles are live.
    /// </para>
    /// </remarks>
    /// <returns>False if there is no such queued print on that printer.</returns>
    public async Task<bool> MoveAsync(int printerId,
                                      Guid printUuid,
                                      Caller caller,
                                      int targetIndex,
                                      CancellationToken cancellationToken)
    {
        // Reordering moves other people's work as well as your own - there is one queue - so it is
        // the same right as withdrawing somebody else's, not the same right as adding your own.
        await _access.RequireAsync(printerId, caller, Capability.ControlPrinter, cancellationToken);

        QueuedPrint? job = await FindAsync(printerId, printUuid, cancellationToken);

        if (job is null)
        {
            return false;
        }

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
    /// <b>A print the printer has confirmed is never stopped from here.</b> Once the printer has
    /// taken it, the entry is gone and the print is a <see cref="PrintJob"/>; stopping it is a
    /// separate deliberate act, so withdrawing from the queue can never cancel a running print on
    /// somebody.
    /// </para>
    /// <para>
    /// <b>A print still being started is, because the person withdrawing it never saw it start.</b>
    /// The entry outlives the <c>START_PRINT</c> until the printer confirms it, and that can take
    /// minutes when the answer is slow. Withdrawing it then is a request that it not print, so the
    /// open row is marked and the queue's loop stops the print once the printer says it is ours and
    /// running - or drops the request if it never began. Nothing is sent from here: the printer holds
    /// one command at a time, and one still deciding has nothing to stop.
    /// </para>
    /// <para>
    /// <b>The entry is deleted before the row is looked for, and the order is the point.</b> The loop
    /// opens its row before sending and looks for the entry again after, so whichever of the two
    /// writes second sees the other's: either the loop finds the entry gone and sends nothing, or this
    /// finds the row and marks it. Looking first would leave a window where neither sees the other and
    /// the print starts unmarked.
    /// </para>
    /// <para>
    /// <b>Whose entry it is decides who may remove it.</b> <see cref="Capability.Print"/> withdraws
    /// your own, <see cref="Capability.ControlPrinter"/> withdraws anybody's: the queue is the
    /// printer's rather than the queuer's, for whoever holds <see cref="Capability.ControlPrinter"/>.
    /// It is the same rule <see cref="PrintStopService"/> applies to a stop, so a withdrawal that may
    /// turn into one is never allowed what the stop would not be.
    /// </para>
    /// </remarks>
    public async Task<QueueCancellation> CancelAsync(int printerId,
                                                     Guid printUuid,
                                                     Caller caller,
                                                     CancellationToken cancellationToken)
    {
        // Whether this caller may withdraw anything here - their own work being the least - asked
        // before the lookup, so somebody who may withdraw nothing is refused alike for a live handle
        // and a dead one. Whose entry it is can only be asked once it is found.
        await _access.RequireWithdrawingAsync(printerId, caller, caller.UserId, cancellationToken);

        QueuedPrint? job = await FindAsync(printerId, printUuid, cancellationToken);

        if (job is null)
        {
            return QueueCancellation.NotFound;
        }

        await _access.RequireWithdrawingAsync(job.PrinterId, caller, job.QueuedByUserId, cancellationToken);

        _dbContext.QueuedPrints.Remove(job);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The printer confirmed the print between the lookup and the delete, and the loop took
            // the entry. It is a print now, which this never stops.
            return QueueCancellation.NotFound;
        }

        int starting = await _dbContext.PrintJobs
                                       .Where(row => row.PrinterId == job.PrinterId &&
                                                     row.PrintUuid == job.PrintUuid &&
                                                     row.EndedAt == null)
                                       .ExecuteUpdateAsync(set => set.SetProperty(row => row.WithdrawnByUserId, caller.UserId)
                                                                     .SetProperty(row => row.WithdrawnByScope, caller.ScopeToRecord),
                                                           cancellationToken);

        return starting > 0 ? QueueCancellation.StopRequested : QueueCancellation.Removed;
    }

    /// <summary>One entry in one printer's queue, tracked for the write that follows.</summary>
    private Task<QueuedPrint?> FindAsync(int printerId, Guid printUuid, CancellationToken cancellationToken)
    {
        return _dbContext.QueuedPrints
                         .SingleOrDefaultAsync(candidate => candidate.PrinterId == printerId &&
                                                            candidate.PrintUuid == printUuid,
                                               cancellationToken);
    }
}
