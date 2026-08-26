using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Host.Printing;
using Homespool.Host.Queue;
using Homespool.Host.Telemetry;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Services;

/// <summary>
/// Deletes a printer, and everything the deployment knows about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own service rather than a method on <see cref="PrinterQueryService"/></b>, which owns the
/// edits a printer's row can take. Those are one <c>SaveChanges</c> each; this is an ordered sequence
/// across three components that has to be right in that order, and folding it in would put a
/// connection registry and a telemetry writer into the constructor of a class every printer page
/// resolves to render a list.
/// </para>
/// <para>
/// <b>The cascades do the actual erasing</b>, and were written expecting this: telemetry, events,
/// live state, tools, queue entries, print history, the printer's copy of any transferred file, its
/// cameras and all three enrolment tables are <c>DeleteBehavior.Cascade</c> from <c>Printer</c> - see
/// <c>HomespoolDbContext</c>, where each one says why. Nothing here deletes a child row by hand.
/// </para>
/// <para>
/// <b>What survives, deliberately:</b> the team, and any <c>PrintFile</c> that was sent to this
/// printer. The file belongs to a person's library rather than to a machine, and its
/// <c>PrintFileOnPrinter</c> row - knowledge about somebody else's drive - is the part that goes.
/// </para>
/// <para>
/// <b>The printer itself is not told.</b> Its token lives in EEPROM, so a deleted printer goes on
/// connecting and being refused until somebody re-registers it at the panel. Sending
/// <c>SET_TOKEN</c> first was considered and left out: it reaches only a printer that is connected,
/// which is the case that needs it least.
/// </para>
/// </remarks>
public class PrinterDeletionService
{
    /// <summary>
    /// The states a <b>connected</b> printer may be deleted in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An allow-set, not a denylist</b> - the same reasoning as
    /// <see cref="PrusaConnect.PrinterPreheatService"/>'s <c>HeatingAllowed</c>: a list of forbidden
    /// states fails open on the one nobody thought of.
    /// </para>
    /// <para>
    /// <c>Error</c> is in. A printer sitting on an error screen is not using itself, and refusing
    /// there would make the machine most likely to be retired the one that cannot be.
    /// <c>Unknown</c> is out, and only means anything here <em>because</em> the printer is connected:
    /// it is this application not having merged the first telemetry yet, which resolves within
    /// seconds - so the refusal says wait, which is true and brief.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlySet<PrinterStatus> DeletionAllowed = new HashSet<PrinterStatus>
    {
        PrinterStatus.Idle,
        PrinterStatus.Ready,
        PrinterStatus.Finished,
        PrinterStatus.Stopped,
        PrinterStatus.Error,
    };

    private readonly HomespoolDbContext _dbContext;
    private readonly PrinterAccessService _access;
    private readonly QueueSnapshotReader _snapshots;
    private readonly PrinterConnectionRegistry _registry;
    private readonly ITelemetryEviction _telemetry;
    private readonly ILogger<PrinterDeletionService> _logger;

    public PrinterDeletionService(HomespoolDbContext dbContext,
                                  PrinterAccessService access,
                                  QueueSnapshotReader snapshots,
                                  PrinterConnectionRegistry registry,
                                  ITelemetryEviction telemetry,
                                  ILogger<PrinterDeletionService> logger)
    {
        _dbContext = dbContext;
        _access = access;
        _snapshots = snapshots;
        _registry = registry;
        _telemetry = telemetry;
        _logger = logger;
    }

    /// <summary>
    /// Deletes the printer with <paramref name="uuid"/>. Answers <c>null</c> if the caller cannot see
    /// it, and the deleted printer's display name otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two questions, two refusal shapes, in this order</b> - as
    /// <see cref="PrinterQueryService.UpdatePrinterAsync"/>: a caller who cannot even read this
    /// printer gets <c>null</c>, because saying "forbidden" would confirm the UUID exists; one who
    /// can read but not manage has already been shown it, so naming the refusal is safe.
    /// </para>
    /// <para>
    /// <b>The state guard applies only while the printer is connected, and that is the load-bearing
    /// part.</b> Nothing ever writes <see cref="PrinterStatus.Offline"/> into
    /// <see cref="PrinterLiveState"/> - a printer that is unplugged mid-print keeps reporting
    /// <c>Printing</c> for ever, because the last thing it said is the last thing it said. Guarding
    /// on that value alone would make the unreachable printer, which is the one most people are
    /// deleting, the single kind that cannot be. So an unreachable printer's state is treated as the
    /// memory it is, and only a printer that is actually connected can refuse.
    /// </para>
    /// </remarks>
    /// <exception cref="TeamAccessDeniedException">Caller lacks <see cref="Capability.ManagePrinter"/>.</exception>
    /// <exception cref="PrinterBusyException">The printer is connected and mid-something.</exception>
    public async Task<string?> DeletePrinterAsync(Guid uuid, Caller caller, CancellationToken cancellationToken)
    {
        if (await _access.FindAsync(uuid, caller, Capability.ViewPrinter, cancellationToken) is null)
        {
            return null;
        }

        Printer printer = await _dbContext.Printers.SingleAsync(p => p.Uuid == uuid, cancellationToken);

        await _access.RequireAsync(printer.Id, caller, Capability.ManagePrinter, cancellationToken);

        QueueSnapshot snapshot = await _snapshots.ReadAsync(printer.Id, cancellationToken);

        if (snapshot.Connected && !DeletionAllowed.Contains(snapshot.Status))
        {
            throw new PrinterBusyException(snapshot.Status);
        }

        string name = printer.Name ?? printer.Model ?? printer.Uuid.ToString();

        // Ordered, and each step depends on the one before it.
        //
        // 1. Close the socket, so the printer stops producing rows that would reference what is about
        //    to be gone. It cannot come back on its own: the credential it authenticates with is in
        //    the enrolment tables this delete cascades away, so its next attempt is a 401.
        _registry.Close(printer.Id);

        // 2. Wait for the writer to drop what it still holds for this printer. A flush commits its
        //    whole batch in one transaction and keeps the buffers when it fails, so one row pointing
        //    at a deleted printer stops telemetry persisting for *every* printer until the buffer
        //    ceilings trim it out. This is the step that makes the delete safe rather than usually
        //    fine; see ITelemetryEviction.
        await _telemetry.ForgetPrinterAsync(printer.Id, cancellationToken);

        // 3. And only now the row, whose cascades take the rest.
        _dbContext.Printers.Remove(printer);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("[{PrinterId}] deleted by {Caller}.", printer.Id, caller.UserId);

        return name;
    }
}
