using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Homespool.Host.Printing;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Queue;

/// <summary>
/// What a queued transfer ending without finishing means for the queue: offered again after a wait on
/// <c>TRANSFER_ABORTED</c> until the retry bound holds it, and held on <c>TRANSFER_STOPPED</c>, which
/// is somebody at the printer saying no.
/// </summary>
/// <remarks>
/// <b>The entry decides</b>, and the entry is the first one for this file on this printer now - the one
/// the queue would send next. One that has gone since the file was sent has nothing left to retry or
/// hold, and the row only stops claiming a partial that has gone.
/// </remarks>
public sealed class QueueTransferEndings : ITransferEndPolicy
{
    private readonly QueueHolds _holds;
    private readonly ILogger<QueueAdvancer> _logger;

    public QueueTransferEndings(TimeProvider timeProvider, ILogger<QueueAdvancer> logger)
    {
        _holds = new QueueHolds(timeProvider, logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task EndedAsync(TransferContext context, PrinterEventType ending, CancellationToken cancellationToken)
    {
        FileOnPrinter row = context.Row!;
        string fileName = QueueAdvancer.ForLog(row.DriveName ?? context.File.Name);

        QueuedPrint? head = await context.DbContext.QueuedPrints
                                         .Include(queued => queued.File)
                                         .Where(queued => queued.PrinterId == context.PrinterId &&
                                                          queued.FileId == row.FileId)
                                         .OrderBy(queued => queued.Position)
                                         .ThenBy(queued => queued.Id)
                                         .FirstOrDefaultAsync(cancellationToken);

        if (head is null)
        {
            _logger.LogInformation("[{PrinterId}] the transfer of {FileName} ended with {EventType} before it finished",
                                   context.PrinterId, fileName, ending);
        }
        else if (ending == PrinterEventType.TransferStopped)
        {
            _holds.HoldStopped(context.DbContext, context.PrinterId, head, row);
        }
        else
        {
            _logger.LogWarning("[{PrinterId}] the printer gave up the transfer of {FileName}; it will be offered again",
                               context.PrinterId, fileName);
            _holds.RecordRefusal(context.DbContext, context.PrinterId, head, row, TransferRetryRules.TransferAbortedCode,
                                 reason: null, PrintHoldReason.TransferAborted);
        }
    }
}
