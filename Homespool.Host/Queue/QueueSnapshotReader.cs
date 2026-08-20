using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.PrintFiles;
using Homespool.Host.Printing;
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
    private readonly PrinterConnectionRegistry _registry;
    private readonly TimeProvider _timeProvider;

    public QueueSnapshotReader(HomespoolDbContext dbContext, PrinterConnectionRegistry registry, TimeProvider timeProvider)
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
                                                        .SingleOrDefaultAsync(
                                                            row => row.PrinterId == printerId &&
                                                                   row.PrintFileId == head.PrintFileId,
                                                            cancellationToken);

        Printer? printer = await _dbContext.Printers
                                           .AsNoTracking()
                                           .SingleOrDefaultAsync(row => row.Id == printerId, cancellationToken);

        List<PrinterTool> tools = await _dbContext.PrinterTools
                                                  .AsNoTracking()
                                                  .Where(tool => tool.PrinterId == printerId)
                                                  .ToListAsync(cancellationToken);

        return new QueueSnapshot(
            _registry.IsConnected(printerId),
            live?.Status ?? PrinterStatus.Unknown,
            new QueueHead(head.Id, head.PrintFileId, head.PrintFile.Name, onPrinter?.Arrived ?? false,
                          onPrinter?.PrinterPath),
            IsTransferInFlight(onPrinter),
            printInFlight,
            CompatibilityHold(head.PrintFile, printer, tools) ?? onPrinter?.HoldReason);
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
    private static PrintHoldReason? CompatibilityHold(PrintFile file,
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
