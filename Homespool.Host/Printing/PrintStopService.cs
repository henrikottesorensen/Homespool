using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Printing;

/// <summary>
/// Stops a running print, and records that a person here asked for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The attribution is the reason this type exists.</b> Sending <see cref="StopPrint"/> needs
/// nothing more than <see cref="PrinterCommandService"/>, and both callers did exactly that until
/// 2026-08-03 - which left <see cref="PrintJob.StoppedByUserId"/> without a writer while a stop verb
/// was reachable from the API and from the printers page. A running print is closed by the queue's
/// loop from telemetry alone, so a stop made here and one made at the panel arrive identically;
/// unless somebody notes the difference at the time, history says "at the panel" about both.
/// (A print stopped before it ever began is the exception, closed here - see
/// <see cref="CloseNeverBegunAsync"/> for why that case is settled and a running one is not.)
/// </para>
/// <para>
/// <b>Not folded into <see cref="PrinterCommandService"/>.</b> That is the permission gate and knows
/// nothing about individual verbs, which is what lets every command share it. Teaching it that one of
/// them means something to the print-history table would be the beginning of a list.
/// </para>
/// </remarks>
public class PrintStopService
{
    private readonly HomespoolDbContext _dbContext;
    private readonly PrinterCommandService _commands;

    private readonly PrinterAccessService _access;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PrintStopService> _logger;

    public PrintStopService(HomespoolDbContext dbContext,
                            PrinterCommandService commands,
                            PrinterAccessService access,
                            TimeProvider timeProvider,
                            ILogger<PrintStopService> logger)
    {
        _dbContext = dbContext;
        _commands = commands;
        _access = access;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Asks the printer to stop, and attributes the stop to the caller when it accepts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The row is found before the command is sent, and written after.</b> Not for tidiness:
    /// <c>STOP_PRINT</c>'s ack means the abort was <i>accepted</i>, not that the print has ended
    /// rather than finished, so the queue's loop may close the
    /// row from telemetry while this call is still in flight. Reading the id first means a close
    /// landing in that window costs the attribution nothing.
    /// </para>
    /// <para>
    /// <b>Nothing is written when the printer refuses.</b> A <c>Rejected</c> "No print to stop" means
    /// the print carried on, and naming a stopper on a print nobody stopped is the same class of
    /// falsehood this column exists to prevent - just pointing the other way.
    /// </para>
    /// <para>
    /// <b>The ownership half of the permission lives here, and only here.</b> <c>StopPrint</c>
    /// declares <see cref="Capability.Print"/> as its floor, which alone would let anyone holding it
    /// stop anyone's print; what narrows that to <i>your own</i> print is
    /// <see cref="PrinterAccessService.RequireWithdrawingAsync"/>, called below. This is the one place
    /// that can make the check, because it is the one place that has read the open row and therefore
    /// knows who queued it - and <b>every path to a stop comes through here</b>, which is what makes
    /// "only here" safe rather than fragile. <see cref="Capability.ControlPrinter"/> stops anybody's.
    /// </para>
    /// <para>
    /// <b>The rest of authorisation is still not duplicated here.</b> Whether this account may touch
    /// this printer at all belongs to <see cref="PrinterCommandService"/>, and a caller who may not
    /// gets an exception out of the send.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The printer's own answer, exactly as <see cref="PrinterCommandService.SendCommandAsync(int, Printing.IPrinterIntent, Homespool.Model.Caller, System.Threading.CancellationToken)"/>
    /// gives it - so callers keep reporting refusals in their own words. Every way the send can fail
    /// throws, as it does there.
    /// </returns>
    public async Task<CommandOutcome?> StopAsync(int printerId, Caller caller, CancellationToken cancellationToken)
    {
        ActivePrint? active = await _dbContext.PrintJobs
                                              .AsNoTracking()
                                              .Where(job => job.PrinterId == printerId && job.EndedAt == null)
                                              .Select(job => new ActivePrint(job.Id, job.QueuedByUserId, job.State))
                                              .SingleOrDefaultAsync(cancellationToken);

        // Withdrawing your own work is Print; withdrawing somebody else's is ControlPrinter. An open
        // row nobody owns cannot happen - QueuedByUserId is required - so a missing row is the only
        // case with nothing to check against, and the send's own gate still applies to it.
        if (active is { } job)
        {
            await _access.RequireWithdrawingAsync(printerId, caller, job.QueuedByUserId, cancellationToken);
        }

        CommandOutcome? outcome = await _commands.SendCommandAsync(printerId, new StopPrint(), caller, cancellationToken);

        if (outcome?.EventType is PrinterEventType.Rejected or PrinterEventType.Failed)
        {
            return outcome;
        }

        if (active is null)
        {
            // Accepted with nothing here to attribute it to - a print started before this process
            // knew about it, or one already closed. Worth a line, because it is the shape of a
            // history row that will later say "at the panel" about a stop that was not.
            _logger.LogDebug("[{PrinterId}] Stop accepted with no open print to attribute it to", printerId);

            return outcome;
        }

        // Still open, or already closed as Stopped - the second is the race this method is arranged
        // around. Deliberately not Finished or Unknown: a print that ended some other way in the
        // seconds after the ack was not stopped by this person, and under-claiming is the safe
        // direction. It costs the attribution in a rare race and never asserts a stop that did not
        // happen.
        //
        // StoppedByUserId == null keeps the first stop rather than the last: two people pressing stop
        // seconds apart both get an accepted ack, and the one that caused it is the first.
        int marked = await _dbContext.PrintJobs
                                     .Where(job => job.Id == active.Id &&
                                                   job.StoppedByUserId == null &&
                                                   (job.EndedAt == null || job.State == PrintState.Stopped))
                                     .ExecuteUpdateAsync(
                                         set => set.SetProperty(job => job.StoppedByUserId, caller.UserId),
                                         cancellationToken);

        if (marked > 0)
        {
            _logger.LogInformation("[{PrinterId}] Print {JobId} stopped by user {UserId}", printerId, active.Id, caller.UserId);
        }

        if (active.State == PrintState.Starting)
        {
            await CloseNeverBegunAsync(printerId, active, cancellationToken);
        }

        return outcome;
    }

    /// <summary>
    /// Closes a print that was stopped before it ever began.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The ambiguity that keeps this method out of the running-print case does not exist here.</b>
    /// An accepted <c>STOP_PRINT</c> means the abort was taken, not that the print ended rather than
    /// finished - which is why a <see cref="PrintState.Printing"/> row is left for the queue's loop to
    /// characterise from telemetry. A <see cref="PrintState.Starting"/> row never began, so there is
    /// no natural completion it could be confused with: accepted stop plus never begun is settled, at
    /// the moment of the ack, with nothing left to observe.
    /// </para>
    /// <para>
    /// <b>The outcome is the reason to do it here rather than leave it to the loop.</b> The loop can
    /// only close such a row as <see cref="PrintState.Unknown"/>, because telemetry cannot say why a
    /// print that never started stopped being reported. This call knows why - a person asked - so the
    /// history row reads <see cref="PrintState.Stopped"/> with a stopper beside it, instead of
    /// <c>Unknown</c> with a stopper beside it, which describes a well-understood event as a mystery.
    /// </para>
    /// <para>
    /// <b>Guarded rather than raced.</b> The loop may close this row in the same moment; the
    /// conditional update simply matches nothing if it got there first, which is the same arrangement
    /// the attribution above uses and needs no coordination between them.
    /// </para>
    /// <para>
    /// <b>It does not replace the loop's own rule.</b> A stop at the printer's panel never reaches
    /// this service, and closes through <c>QueueAdvancer</c> on the job id being withdrawn. This is
    /// the earlier and better-informed of two paths to the same place, not the only one.
    /// </para>
    /// </remarks>
    private async Task CloseNeverBegunAsync(int printerId, ActivePrint active, CancellationToken cancellationToken)
    {
        int closed = await _dbContext.PrintJobs
                                     .Where(job => job.Id == active.Id &&
                                                   job.EndedAt == null &&
                                                   job.State == PrintState.Starting)
                                     .ExecuteUpdateAsync(
                                         set => set.SetProperty(job => job.State, PrintState.Stopped)
                                                   .SetProperty(job => job.EndedAt, _timeProvider.GetUtcNow()),
                                         cancellationToken);

        if (closed > 0)
        {
            _logger.LogInformation(
                "[{PrinterId}] Print {JobId} was stopped before it began; closing it rather than holding the queue.",
                printerId, active.Id);
        }
    }

    /// <summary>The open print, reduced to what a stop needs: which row, whose work, and its phase.</summary>
    private sealed record ActivePrint(long Id, long QueuedByUserId, PrintState State);
}
