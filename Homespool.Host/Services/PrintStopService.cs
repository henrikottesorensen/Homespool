using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Services;

/// <summary>
/// Stops a running print, and records that a person here asked for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The attribution is the reason this type exists.</b> Sending <see cref="StopPrint"/> needs
/// nothing more than <see cref="PrinterCommandService"/>, and both callers did exactly that until
/// 2026-08-03 - which left <see cref="PrintJob.StoppedByUserId"/> without a writer while a stop verb
/// was reachable from the API and from the printers page. The queue's loop closes the row from
/// telemetry alone, so a stop made here and one made at the panel arrive identically; unless somebody
/// notes the difference at the time, history says "at the panel" about both.
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
    private readonly ILogger<PrintStopService> _logger;

    public PrintStopService(HomespoolDbContext dbContext,
                            PrinterCommandService commands,
                            PrinterAccessService access,
                            ILogger<PrintStopService> logger)
    {
        _dbContext = dbContext;
        _commands = commands;
        _access = access;
        _logger = logger;
    }

    /// <summary>
    /// Asks the printer to stop, and attributes the stop to <paramref name="userId"/> when it accepts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The row is found before the command is sent, and written after.</b> Not for tidiness:
    /// <c>STOP_PRINT</c>'s ack means the abort was <i>accepted</i>, not that the print has ended
    /// (<c>notes/print-queue.md</c>, "Stop, and the ack that lies"), so the queue's loop may close the
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
    /// The printer's own answer, exactly as <see cref="PrinterCommandService.SendCommandAsync"/>
    /// gives it - so callers keep reporting refusals in their own words. Every way the send can fail
    /// throws, as it does there.
    /// </returns>
    public async Task<CommandOutcome?> StopAsync(int printerId, long userId, CancellationToken cancellationToken)
    {
        ActivePrint? active = await _dbContext.PrintJobs
                                              .AsNoTracking()
                                              .Where(job => job.PrinterId == printerId && job.EndedAt == null)
                                              .Select(job => new ActivePrint(job.Id, job.QueuedByUserId))
                                              .SingleOrDefaultAsync(cancellationToken);

        // Withdrawing your own work is Print; withdrawing somebody else's is ControlPrinter. An open
        // row nobody owns cannot happen - QueuedByUserId is required - so a missing row is the only
        // case with nothing to check against, and the send's own gate still applies to it.
        if (active is { } job)
        {
            await _access.RequireWithdrawingAsync(printerId, userId, job.QueuedByUserId, cancellationToken);
        }

        CommandOutcome? outcome = await _commands.SendCommandAsync(printerId, new StopPrint(), userId, cancellationToken);

        if (outcome?.EventType is Events.Rejected or Events.Failed)
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
                                     .Where(job => job.Id == active.Id
                                                   && job.StoppedByUserId == null
                                                   && (job.EndedAt == null || job.State == PrintState.Stopped))
                                     .ExecuteUpdateAsync(
                                         set => set.SetProperty(job => job.StoppedByUserId, userId),
                                         cancellationToken);

        if (marked > 0)
        {
            _logger.LogInformation("[{PrinterId}] Print {JobId} stopped by user {UserId}", printerId, active.Id, userId);
        }

        return outcome;
    }

    /// <summary>The open print, reduced to the two things a stop needs: which row, and whose work.</summary>
    private sealed record ActivePrint(long Id, long QueuedByUserId);
}
