using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Host.Exceptions;
using Homespool.Model;

namespace Homespool.Host.Printing;

/// <summary>
/// Cancels, and un-cancels, single objects of a running print - for the person who queued it, and
/// nobody else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stricter than a stop, deliberately.</b> <see cref="PrintStopService"/> lets
/// <see cref="Capability.ControlPrinter"/> stop anybody's print, because stopping a machine is running
/// it. Choosing which of somebody's parts get made is editing their work, and running the printer does
/// not extend to that - so the owner holding <see cref="Capability.Print"/> is the only way in, and a
/// print with no open row here, one started at the panel, is cancelled at the panel.
/// </para>
/// <para>
/// <b>The ownership check lives here, and every path to a cancel comes through here</b>, which is
/// what makes "only here" safe. The intents declare <see cref="Capability.Print"/> as their floor, so
/// <see cref="PrinterCommandService"/> alone would let anybody holding it cancel anybody's objects.
/// </para>
/// </remarks>
public class PrintObjectService
{
    private readonly HomespoolDbContext _dbContext;
    private readonly TelemetryDbContext _telemetry;
    private readonly PrinterCommandService _commands;
    private readonly ILogger<PrintObjectService> _logger;

    public PrintObjectService(HomespoolDbContext dbContext,
                              TelemetryDbContext telemetry,
                              PrinterCommandService commands,
                              ILogger<PrintObjectService> logger)
    {
        _dbContext = dbContext;
        _telemetry = telemetry;
        _commands = commands;
        _logger = logger;
    }

    /// <summary>
    /// Whether <paramref name="caller"/> queued the print this printer has open - the question the
    /// page asks before offering the buttons, and the one <see cref="CancelAsync"/> asks again.
    /// </summary>
    public async Task<bool> OwnsRunningPrintAsync(int printerId, Caller caller, CancellationToken cancellationToken)
    {
        System.ArgumentNullException.ThrowIfNull(caller);

        long? owner = await _dbContext.PrintJobs
                                      .AsNoTracking()
                                      .Where(job => job.PrinterId == printerId && job.EndedAt == null)
                                      .Select(job => (long?)job.QueuedByUserId)
                                      .SingleOrDefaultAsync(cancellationToken);

        return owner == caller.UserId;
    }

    /// <summary>Stops printing one object, and carries on with the rest.</summary>
    /// <param name="printerId">The printer running the print.</param>
    /// <param name="objectId">The object, <b>0-based</b> as the printer reports it.</param>
    /// <param name="caller">Who is asking. Must have queued the print, and hold <see cref="Capability.Print"/>.</param>
    /// <param name="cancellationToken">The caller's own cancellation.</param>
    /// <returns>The printer's own answer, as <see cref="PrinterCommandService"/> gives it.</returns>
    /// <exception cref="TeamAccessDeniedException">The caller did not queue the running print.</exception>
    /// <exception cref="NoSuchObjectException">The printer has not reported that object as cancellable.</exception>
    public Task<CommandOutcome?> CancelAsync(int printerId, int objectId, Caller caller, CancellationToken cancellationToken)
    {
        return SendAsync(printerId, objectId, new CancelObject(objectId), caller, cancellationToken);
    }

    /// <summary>Takes back a cancel, so the object prints again from the current layer.</summary>
    /// <inheritdoc cref="CancelAsync" path="/param"/>
    /// <inheritdoc cref="CancelAsync" path="/returns"/>
    /// <inheritdoc cref="CancelAsync" path="/exception"/>
    public Task<CommandOutcome?> UncancelAsync(int printerId, int objectId, Caller caller, CancellationToken cancellationToken)
    {
        return SendAsync(printerId, objectId, new UncancelObject(objectId), caller, cancellationToken);
    }

    private async Task<CommandOutcome?> SendAsync(int printerId,
                                                  int objectId,
                                                  IPrinterIntent intent,
                                                  Caller caller,
                                                  CancellationToken cancellationToken)
    {
        if (!await OwnsRunningPrintAsync(printerId, caller, cancellationToken))
        {
            throw new TeamAccessDeniedException();
        }

        // The printer's own count, as it last reported it. A stale count can only be too small by the
        // objects of a print that has not been reported yet, which refuses rather than guesses.
        int? count = await _telemetry.PrinterLiveStates
                                     .AsNoTracking()
                                     .Where(state => state.PrinterId == printerId)
                                     .Select(state => state.CancellableObjectCount)
                                     .SingleOrDefaultAsync(cancellationToken);

        if (objectId < 0 || objectId >= (count ?? 0))
        {
            throw new NoSuchObjectException(printerId, objectId);
        }

        CommandOutcome? outcome = await _commands.SendCommandAsync(printerId, intent, caller, cancellationToken);

        if (outcome?.EventType is not (PrinterEventType.Rejected or PrinterEventType.Failed))
        {
            _logger.LogInformation("[{PrinterId}] {Intent} {ObjectId} by user {UserId}",
                                   printerId, intent.Name, objectId, caller.UserId);
        }

        return outcome;
    }
}
