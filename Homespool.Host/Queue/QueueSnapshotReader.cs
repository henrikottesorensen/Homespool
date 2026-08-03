using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.PrusaConnect;
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
    private readonly HSDbContext _dbContext;
    private readonly PrinterConnectionRegistry _registry;
    private readonly TimeProvider _timeProvider;

    public QueueSnapshotReader(HSDbContext dbContext, PrinterConnectionRegistry registry, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _registry = registry;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Whether a transfer this printer is pulling is still worth waiting for.
    /// </summary>
    /// <remarks>
    /// A stale stamp reads as "no transfer" rather than "a transfer": the alternative is a queue that
    /// never advances again after a restart caught one mid-flight. See
    /// <see cref="QueueAdvancer.TransferStaleAfter"/>.
    /// </remarks>
    public bool IsTransferInFlight(PrintFileOnPrinter? onPrinter)
    {
        return onPrinter?.TransferStartedAt is { } startedAt
            && _timeProvider.GetUtcNow() - startedAt < QueueAdvancer.TransferStaleAfter;
    }

    /// <summary>Reads the situation for one printer.</summary>
    public async Task<QueueSnapshot> ReadAsync(int printerId, CancellationToken cancellationToken)
    {
        QueuedPrint? head = await _dbContext.QueuedPrints
                                            .AsNoTracking()
                                            .Include(queued => queued.PrintFile)
                                            .Where(queued => queued.PrinterId == printerId)
                                            .OrderBy(queued => queued.Position)
                                            .ThenBy(queued => queued.Id)
                                            .FirstOrDefaultAsync(cancellationToken);

        PrinterLiveState? live = await _dbContext.PrinterLiveStates
                                                 .AsNoTracking()
                                                 .SingleOrDefaultAsync(state => state.PrinterId == printerId,
                                                     cancellationToken);

        bool printInFlight = await _dbContext.PrintJobs
                                             .AsNoTracking()
                                             .AnyAsync(job => job.PrinterId == printerId && job.EndedAt == null,
                                                 cancellationToken);

        if (head?.PrintFile is null)
        {
            return new QueueSnapshot(_registry.IsConnected(printerId), live?.Status ?? PrinterStatus.Unknown,
                Head: null, TransferInFlight: false, printInFlight);
        }

        PrintFileOnPrinter? onPrinter = await _dbContext.PrintFilesOnPrinters
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.PrinterId == printerId && row.PrintFileId == head.PrintFileId,
                cancellationToken);

        return new QueueSnapshot(
            _registry.IsConnected(printerId),
            live?.Status ?? PrinterStatus.Unknown,
            new QueueHead(head.Id, head.PrintFileId, head.PrintFile.Name, onPrinter?.Arrived ?? false,
                onPrinter?.PrinterPath),
            IsTransferInFlight(onPrinter),
            printInFlight,
            onPrinter?.BlockedReason);
    }
}
