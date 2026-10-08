using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.Firmware;
using Homespool.Host.PrintFiles;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect.Transfers;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Queue;

/// <summary>
/// Gathers everything <see cref="QueueRules.Decide"/> looks at, for one printer, at one moment.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared between the loop and everything that wants to explain the loop</b> - which is the whole
/// reason it exists as a type rather than a method on <see cref="QueueAdvancer"/>. A page that built
/// its own snapshot would drift from the advancer's the first time either changed, and the failure is
/// nasty: the page would confidently state something the loop does not believe. One builder means
/// "what would the loop do right now?" has exactly one answer.
/// </para>
/// <para>
/// <b>Read-only, and it stays that way.</b> The advancer reconciles arrivals and prints *before*
/// asking - those are writes, and a read path must not do them. That also means a caller reading this
/// sees the world as of the last pass, which is the honest thing to show: it is what the loop is
/// acting on.
/// </para>
/// <para>
/// The advancer loads the head again, tracked, because it goes on to remove it. Reading it twice on a
/// handful of printers every few seconds is not worth complicating this into something that hands
/// back tracked entities.
/// </para>
/// </remarks>
public class QueueSnapshotReader
{
    private readonly HomespoolDbContext _dbContext;

    /// <summary>
    /// The printer's last-known state, which <c>StorageOptions.TelemetryInMemory</c> may hold in a
    /// database of its own. Read by printer id, so the queue never has to join across the two.
    /// </summary>
    private readonly TelemetryDbContext _telemetry;
    private readonly PrinterConnectionRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private readonly PrinterAccessService _access;
    private readonly ITransferOffers _offers;
    private readonly IFirmwareInstallations _installations;

    public QueueSnapshotReader(HomespoolDbContext dbContext,
                               TelemetryDbContext telemetry,
                               PrinterConnectionRegistry registry,
                               TimeProvider timeProvider,
                               PrinterAccessService access,
                               ITransferOffers offers,
                               IFirmwareInstallations installations)
    {
        _dbContext = dbContext;
        _telemetry = telemetry;
        _registry = registry;
        _timeProvider = timeProvider;
        _access = access;
        _offers = offers;
        _installations = installations;
    }

    /// <summary>
    /// Whether a transfer this printer is pulling is still worth waiting for.
    /// </summary>
    /// <param name="onPrinter">The <i>(file, printer)</i> row, or null when nothing has been sent.</param>
    /// <param name="fileName">The stored file's name, as its offer records it.</param>
    /// <remarks>
    /// <para>
    /// <b>The stamp says a transfer was started; it takes an observation to say one is running.</b>
    /// Two count, and which one depends on whether the printer has said anything yet:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Once it has reported the file</b> - the <c>FILE_INFO</c> firmware sends a few seconds
    /// in, which set <see cref="FileOnPrinter.PrinterPath"/> - only the transfer's own terminal event
    /// ends it. The offer is not asked: <c>TRANSFER_FINISHED</c> releases it at once, while the event
    /// reaches the database at the writer's next flush, and a pass in that gap would offer the file
    /// again while it is being printed.</item>
    /// <item><b>Before that</b>, the offer is the evidence. Standing, the printer may be pulling it; gone,
    /// it never took the command - a send that failed revokes it, one never collected is swept. A
    /// collected offer keeps answering for a minute after it ends, because a small file can start and
    /// finish before the printer's report of either has been flushed to the event log.</item>
    /// </list>
    /// <para>
    /// A stale stamp still reads as "no transfer" rather than "a transfer": the bound for a report
    /// that never comes. See <see cref="QueueAdvancer.TransferStaleAfter"/>.
    /// </para>
    /// </remarks>
    public bool IsTransferInFlight(FileOnPrinter? onPrinter, string fileName)
    {
        if (onPrinter?.TransferStartedAt is not DateTimeOffset startedAt ||
            _timeProvider.GetUtcNow() - startedAt >= QueueAdvancer.TransferStaleAfter)
        {
            return false;
        }

        return onPrinter.PrinterPath is not null || _offers.IsOffered(onPrinter.PrinterId, fileName);
    }

    /// <summary>
    /// The printer's status if it has said it to the connection it holds now, and
    /// <see cref="PrinterStatus.Unknown"/> otherwise.
    /// </summary>
    /// <param name="live">The stored live state, or null when the printer has never reported.</param>
    /// <param name="connectedSince">
    /// When its current connection registered - <see cref="PrinterConnectionRegistry.ConnectedSince"/> -
    /// or null when it is not connected.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>A stored status is the last thing the printer said, not what it is now.</b> Nothing resets it
    /// when the link drops, and a restart restores it. In the gap somebody can print at the panel, take
    /// the printer out of <c>Ready</c>, or power-cycle it, and the stored <c>Ready</c> still stands when
    /// it comes back - while both transports wake the queue the instant it connects, before it has
    /// reported anything. Read as current, that <c>Ready</c> starts the head onto whatever is on the bed.
    /// </para>
    /// <para>
    /// <b>Tied to the connection rather than to the process</b>, unlike the advancer's guard on closing
    /// an open print, because a dropped link loses the printer exactly as a restart does. Everything
    /// that gates a command on this status - the queue, preheating, unloading, removal - waits for the
    /// first report instead, which arrives within seconds of a connection.
    /// </para>
    /// <para>
    /// <b>Compared in whole milliseconds</b>, the precision <see cref="PrinterLiveState.LastSeenAt"/> is
    /// stored at. Against the registration's full precision, a report received after the connection
    /// registered but within the same millisecond comes back from the database earlier than the
    /// connection it arrived on, and is refused. The cost is admitting a report from the previous
    /// connection made in that same millisecond, which a link that has dropped, or a process that has
    /// restarted, cannot deliver.
    /// </para>
    /// </remarks>
    public static PrinterStatus StatedSinceConnecting(PrinterLiveState? live, DateTimeOffset? connectedSince)
    {
        return live is not null &&
               connectedSince is DateTimeOffset since &&
               live.LastSeenAt.ToUnixTimeMilliseconds() >= since.ToUnixTimeMilliseconds() ?
                   live.Status :
                   PrinterStatus.Unknown;
    }

    /// <summary>Reads the situation for one printer.</summary>
    public async Task<QueueSnapshot> ReadAsync(int printerId, CancellationToken cancellationToken)
    {
        QueuedPrint? head = await _dbContext.QueuedPrints
                                            .AsNoTracking()
                                            .Include(queued => queued.File)
                                            .Where(queued => queued.PrinterId == printerId)
                                            .OrderBy(queued => queued.Position)
                                            .ThenBy(queued => queued.Id)
                                            .FirstOrDefaultAsync(cancellationToken);

        PrinterLiveState? live = await _telemetry.PrinterLiveStates
                                                 .AsNoTracking()
                                                 .SingleOrDefaultAsync(state => state.PrinterId == printerId,
                                                                       cancellationToken);

        DateTimeOffset? connectedSince = _registry.ConnectedSince(printerId);
        bool connected = connectedSince is not null;
        PrinterStatus status = StatedSinceConnecting(live, connectedSince);

        bool printInFlight = await _dbContext.PrintJobs
                                             .AsNoTracking()
                                             .AnyAsync(job => job.PrinterId == printerId && job.EndedAt == null,
                                                       cancellationToken);

        if (head?.File is null)
        {
            return new QueueSnapshot(connected, status, Head: null, TransferInFlight: false, printInFlight,
                                     FirmwareInstalling: _installations.IsInstalling(printerId));
        }

        FileOnPrinter? onPrinter = await _dbContext.FilesOnPrinters
                                                   .AsNoTracking()
                                                   .SingleOrDefaultAsync(
                                                       row => row.PrinterId == printerId &&
                                                              row.FileId == head.FileId,
                                                       cancellationToken);

        Printer? printer = await _dbContext.Printers
                                           .AsNoTracking()
                                           .SingleOrDefaultAsync(row => row.Id == printerId, cancellationToken);

        List<PrinterTool> tools = await _dbContext.PrinterTools
                                                  .AsNoTracking()
                                                  .Where(tool => tool.PrinterId == printerId)
                                                  .ToListAsync(cancellationToken);

        // The question every send the loop makes for this entry will ask - the same service, the
        // same authority and the same capability - so the page cannot say "sending" about a file the
        // gate will refuse, and the loop does not spend a pass finding that out.
        bool authorityLapsed = !await _access.AllowsAsync(printerId, QueueAdvancer.CallerFor(head),
                                                          Capability.Print, cancellationToken);

        // The copy on the drive counts only while it is this version of the file. An overwrite leaves
        // the old bytes arrived and named under the new digest, and a copy nobody recorded a digest
        // for may be anything - so either reads as nothing there, and the rules send the file instead
        // of printing what is. A transfer of an older version still running is left to finish: the
        // printer has one transfer slot, and its path is hidden so nothing prints the partial.
        bool current = PrinterDriveCopies.IsCurrent(onPrinter, head.File.Digest);

        return new QueueSnapshot(
            connected,
            status,
            new QueueHead(head.Id, head.FileId, head.File.Name, current && onPrinter!.Arrived,
                          current ? onPrinter!.PrinterPath : null),
            IsTransferInFlight(onPrinter, head.File.Name),
            printInFlight,
            CompatibilityHold(head.File, printer, tools) ?? onPrinter?.HoldReason,
            TransferRetryRules.IsWaiting(onPrinter, _timeProvider.GetUtcNow()),
            authorityLapsed,
            TransferRetryRules.IsCountingAborts(onPrinter),
            _installations.IsInstalling(printerId));
    }

    /// <summary>
    /// The hold a file-versus-printer disagreement amounts to, or null when there is none serious
    /// enough to stop a print.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Computed here rather than stored, which is what makes it clear itself.</b> Every other hold
    /// is a fact somebody had to go and discover - how much room the drive has, what is already on it
    /// - so it is written down and cleared by whoever re-discovers it. This one is a comparison of
    /// two rows already in hand, so remembering it would only create something to forget: fit a
    /// hardened nozzle, let the printer say so, and the next read simply finds nothing wrong.
    /// </para>
    /// <para>
    /// <b>It outranks a stored hold when both apply</b>, and not merely because it is more serious.
    /// A stored space hold routes the advancer back into the transfer path so the drive can be
    /// re-asked; sending a file that must not be printed is harmless but pointless, and reporting
    /// "not enough room" about a print that would damage a nozzle is the wrong sentence entirely.
    /// </para>
    /// <para>
    /// <b>Only the <see cref="PrintCompatibilitySeverity.Hold"/> findings reach here.</b> A warning
    /// belongs where a person is standing - at the queue attempt and on the page - not in a loop that
    /// nobody is watching.
    /// </para>
    /// </remarks>
    private static PrintHoldReason? CompatibilityHold(HSFile file,
                                                      Printer? printer,
                                                      IReadOnlyList<PrinterTool> tools)
    {
        if (printer is null)
        {
            return null;
        }

        foreach (PrintCompatibilityFinding finding in PrintFileCompatibility.Evaluate(file, printer, tools))
        {
            switch (finding)
            {
                case PrintCompatibilityFinding.AbrasiveFilamentNeedsHardenedNozzle:
                    return PrintHoldReason.AbrasiveFilamentNeedsHardenedNozzle;

                case PrintCompatibilityFinding.IncompatiblePrinterModel:
                    return PrintHoldReason.IncompatiblePrinterModel;

                default:
                    break;
            }
        }

        return null;
    }
}
