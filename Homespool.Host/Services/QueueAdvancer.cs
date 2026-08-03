using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Services;

/// <summary>
/// The producer loop: for each printer, work out what its queue needs next and do it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A hosted service rather than anything inside <c>PrinterConnectionActor</c></b>
/// (<c>notes/print-queue.md</c>). The actor has no database access by design - that is what keeps its
/// loop single-threaded and free of permission checks - so the thing that reads queues and writes
/// rows lives out here and talks to the actor the same way a person does, through
/// <see cref="PrinterCommandService"/>.
/// </para>
/// <para>
/// <b>It acts as the user who queued the print.</b> The loop is not a principal and must not become a
/// way around <c>TeamMember.CanUse</c>: every command goes out under
/// <see cref="QueuedPrint.QueuedByUserId"/>, so a member whose access is revoked between queueing and
/// printing simply stops advancing. That is also the only handle on <i>whose</i> file it is, since the
/// store is keyed by user.
/// </para>
/// <para>
/// <b>Everything it needs is persisted, so a tick is stateless.</b> It holds no per-printer memory
/// between passes beyond the event watermark, which is an optimisation rather than state: losing it
/// costs a re-scan, not correctness. A restart therefore resumes without ceremony, and the design's
/// "nudged on enqueue and on connect" is a latency improvement over the timer rather than the
/// mechanism.
/// </para>
/// </remarks>
public sealed class QueueAdvancer : BackgroundService
{
    /// <summary>
    /// How often the loop looks, absent a poke. Slow on purpose: the things it waits for are a
    /// transfer finishing and a person clearing a bed, both measured in minutes, and
    /// <see cref="QueueSignal"/> covers the one case where a human is watching.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a transfer may sit unfinished before the loop stops waiting on it and offers the file
    /// again.
    /// </summary>
    /// <remarks>
    /// Generous, because a full-size model over TLS is minutes - 279.8 KB/s measured through nginx, so
    /// 100 MB is close to six. This exists for the case with no other bound: a server restarted
    /// mid-transfer leaves <see cref="PrintFileOnPrinter.TransferStartedAt"/> set with nothing running
    /// and no terminal event ever coming, which without this would wedge that printer's queue
    /// permanently.
    /// </remarks>
    public static readonly TimeSpan TransferStaleAfter = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PrinterConnectionRegistry _registry;
    private readonly QueueSignal _signal;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<QueueAdvancer> _logger;

    /// <summary>Last <c>PrinterEvent</c> id examined per printer - see the class remarks.</summary>
    private readonly Dictionary<int, long> _watermarks = [];

    /// <summary>
    /// One pass at a time per printer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Found by a unique-index violation, not by reasoning</b> (2026-08-03). An earlier comment
    /// here claimed passes could not overlap; nothing made that true. The timer and an explicit call
    /// overlapped in a test, and in production the same happens whenever a pass outlives the poll
    /// interval - a slow command, a printer taking its time to answer.
    /// </para>
    /// <para>
    /// The duplicate row was the symptom and the cheap half. The real fault is that both passes read
    /// "not arrived, nothing in flight" and both offer the file, so the printer is sent the same
    /// transfer twice - which firmware's single transfer slot then refuses, leaving a queue that looks
    /// stuck for a reason no log line explains.
    /// </para>
    /// <para>
    /// <b>Skip rather than queue.</b> A pass that cannot get the gate has nothing to add: the pass
    /// already running reads the same state and will act on it. Waiting would only stack ticks up
    /// behind a slow printer.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _perPrinter = new();

    public QueueAdvancer(IServiceScopeFactory scopeFactory,
                         PrinterConnectionRegistry registry,
                         QueueSignal signal,
                         TimeProvider timeProvider,
                         ILogger<QueueAdvancer> logger)
    {
        _scopeFactory = scopeFactory;
        _registry = registry;
        _signal = signal;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(PollInterval, stoppingToken);
                await AdvanceAllAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>One pass over every printer with something queued. Public so a test can drive it.</summary>
    /// <remarks>
    /// Sequential rather than concurrent, deliberately. This is one-to-tens of printers, each pass is
    /// a handful of queries and at most one command, and a command returns as soon as the printer
    /// accepts it - a transfer's bytes move afterwards, on the actor's own loop, not here.
    /// <para>
    /// Sequential here does <b>not</b> mean passes cannot overlap - a caller outside this loop can
    /// start one, and a slow pass outlives its own tick. That is what <see cref="_perPrinter"/> is
    /// for, and assuming otherwise was a real defect.
    /// </para>
    /// </remarks>
    public async Task AdvanceAllAsync(CancellationToken cancellationToken)
    {
        List<int> printerIds;

        await using (AsyncServiceScope scope = _scopeFactory.CreateAsyncScope())
        {
            HSDbContext dbContext = scope.ServiceProvider.GetRequiredService<HSDbContext>();

            printerIds = await dbContext.QueuedPrints
                                        .Select(queued => queued.PrinterId)
                                        .Distinct()
                                        .ToListAsync(cancellationToken);
        }

        foreach (int printerId in printerIds)
        {
            try
            {
                await AdvanceAsync(printerId, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // One printer's problem must not stop the others, and must not kill the loop: the
                // next tick tries again, which is the right response to almost everything that can
                // go wrong here (a printer dropping mid-command, a transient database error).
                _logger.LogError(e, "Advancing the queue for printer {PrinterId} failed.", printerId);
            }
        }
    }

    /// <summary>Works out what one printer needs and does it.</summary>
    public async Task AdvanceAsync(int printerId, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = _perPrinter.GetOrAdd(printerId, _ => new SemaphoreSlim(1, 1));

        if (!await gate.WaitAsync(0, cancellationToken))
        {
            _logger.LogDebug("[{PrinterId}] a pass is already running; leaving it to that one", printerId);

            return;
        }

        try
        {
            await AdvanceOnceAsync(printerId, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task AdvanceOnceAsync(int printerId, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        HSDbContext dbContext = scope.ServiceProvider.GetRequiredService<HSDbContext>();

        // Read the printer's own reports first, so a transfer that finished since the last pass is
        // known before anything is decided on the assumption that it has not.
        await ReconcileArrivalsAsync(dbContext, printerId, cancellationToken);

        QueuedPrint? head = await dbContext.QueuedPrints
                                           .Include(queued => queued.PrintFile)
                                           .Where(queued => queued.PrinterId == printerId)
                                           .OrderBy(queued => queued.Position)
                                           .ThenBy(queued => queued.Id)
                                           .FirstOrDefaultAsync(cancellationToken);

        if (head?.PrintFile is null)
        {
            return;
        }

        PrintFileOnPrinter? onPrinter = await dbContext.PrintFilesOnPrinters
            .SingleOrDefaultAsync(row => row.PrinterId == printerId && row.PrintFileId == head.PrintFileId,
                cancellationToken);

        PrinterLiveState? liveState = await dbContext.PrinterLiveStates
                                                     .AsNoTracking()
                                                     .SingleOrDefaultAsync(state => state.PrinterId == printerId,
                                                         cancellationToken);

        QueueSnapshot snapshot = new(
            Connected: _registry.IsConnected(printerId),
            liveState?.Status ?? PrinterStatus.Unknown,
            new QueueHead(head.Id, head.PrintFileId, head.PrintFile.Name,
                onPrinter?.Arrived ?? false, onPrinter?.PrinterPath),
            TransferInFlight: IsTransferInFlight(onPrinter));

        QueueAction action = QueueRules.Decide(snapshot);

        switch (action.Kind)
        {
            case QueueActionKind.Transfer:
                await TransferAsync(scope, dbContext, printerId, head, onPrinter, cancellationToken);
                break;

            case QueueActionKind.Print:
                await PrintAsync(scope, dbContext, printerId, head, action.Head!.PrinterPath!, cancellationToken);
                break;

            case QueueActionKind.Wait:
                _logger.LogDebug("[{PrinterId}] queue holding: {Reason}", printerId, action.Reason);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Whether a transfer this printer is pulling is still worth waiting for.
    /// </summary>
    /// <remarks>
    /// A stale timestamp reads as "no transfer" rather than "a transfer": the alternative is a queue
    /// that never advances again after a restart caught one mid-flight, and offering the file a second
    /// time is harmless - the printer either takes it or answers that its transfer slot is busy, which
    /// is the same waiting the loop was doing anyway.
    /// </remarks>
    private bool IsTransferInFlight(PrintFileOnPrinter? onPrinter)
    {
        return onPrinter?.TransferStartedAt is { } startedAt
            && _timeProvider.GetUtcNow() - startedAt < TransferStaleAfter;
    }

    /// <summary>
    /// Turns the printer's own reports into arrival: a <c>FILE_INFO</c> naming one of our files marks
    /// it present and records the path the printer calls it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Arrival is the <c>FILE_INFO</c>, not the <c>TRANSFER_FINISHED</c></b>, because the path to
    /// print with only exists in the former. Connect does the same - it transfers to the long name and
    /// starts the print with the 8.3 name the answering <c>FILE_INFO</c> reports - and deriving that
    /// name ourselves would mean inventing a <c>~N</c> collision index against a directory we cannot
    /// see, where a wrong guess prints a different file.
    /// </para>
    /// <para>
    /// Matched on <c>display_name</c>, which is the only field carrying the name we sent. A transfer
    /// whose <c>FILE_INFO</c> never arrives leaves the row unarrived, and the staleness timeout above
    /// eventually offers the file again rather than waiting forever.
    /// </para>
    /// </remarks>
    private async Task ReconcileArrivalsAsync(HSDbContext dbContext, int printerId,
        CancellationToken cancellationToken)
    {
        long watermark = _watermarks.TryGetValue(printerId, out long last) ? last : 0;

        List<PrinterEvent> events = await dbContext.PrinterEvents
            .AsNoTracking()
            .Where(printerEvent => printerEvent.PrinterId == printerId
                                   && printerEvent.Id > watermark
                                   && printerEvent.EventType == Events.FileInfo)
            .OrderBy(printerEvent => printerEvent.Id)
            .ToListAsync(cancellationToken);

        long highest = await dbContext.PrinterEvents
                                      .Where(printerEvent => printerEvent.PrinterId == printerId)
                                      .MaxAsync(printerEvent => (long?)printerEvent.Id, cancellationToken) ?? watermark;

        bool changed = false;

        foreach (PrinterEvent printerEvent in events)
        {
            if (printerEvent.Payload is not { } payload)
            {
                continue;
            }

            FileInfoEventDataDTO? data;

            try
            {
                data = JsonSerializer.Deserialize<FileInfoEventDataDTO>(payload);
            }
            catch (JsonException)
            {
                // Stored verbatim from the wire, so this is a printer sending something unmodelled
                // rather than our own corruption. Not this loop's business to complain about.
                continue;
            }

            if (data?.DisplayName is not { } displayName || data.Path is null)
            {
                continue;
            }

            PrintFileOnPrinter? row = await dbContext.PrintFilesOnPrinters
                .Include(candidate => candidate.PrintFile)
                .SingleOrDefaultAsync(candidate => candidate.PrinterId == printerId
                                                   && candidate.PrintFile!.Name == displayName,
                    cancellationToken);

            if (row is null || row.Arrived)
            {
                continue;
            }

            row.ArrivedAt = printerEvent.Timestamp;
            row.PrinterPath = data.Path;
            row.TransferStartedAt = null;
            changed = true;

            _logger.LogInformation("[{PrinterId}] {FileName} is on the drive as {PrinterPath}",
                printerId, displayName, data.Path);
        }

        if (changed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        _watermarks[printerId] = highest;
    }

    /// <summary>Offers the head's file to the printer, and records that it did.</summary>
    private async Task TransferAsync(AsyncServiceScope scope, HSDbContext dbContext, int printerId,
        QueuedPrint head, PrintFileOnPrinter? onPrinter, CancellationToken cancellationToken)
    {
        PrintFileCatalog catalog = scope.ServiceProvider.GetRequiredService<PrintFileCatalog>();
        StoredFile? file = catalog.Find(head.QueuedByUserId, head.PrintFile!.Name);

        if (file is null)
        {
            // The bytes went while the entry waited. Nothing to send and nothing to wait for, so the
            // entry is dropped rather than retried forever - the reconciler makes the same call when
            // it finds a row whose file has left.
            _logger.LogWarning("[{PrinterId}] {FileName} is queued but no longer on disk; dropping the entry",
                printerId, head.PrintFile.Name);
            dbContext.QueuedPrints.Remove(head);
            await dbContext.SaveChangesAsync(cancellationToken);

            return;
        }

        Printer printer = await dbContext.Printers.SingleAsync(candidate => candidate.Id == printerId,
            cancellationToken);

        PrintFileSender sender = scope.ServiceProvider.GetRequiredService<PrintFileSender>();

        // Recorded before the send rather than after: the printer can begin asking for chunks the
        // instant it accepts, and a row written afterwards would leave a window in which the next tick
        // saw no transfer and offered the file again.
        onPrinter ??= new PrintFileOnPrinter { PrinterId = printerId, PrintFileId = head.PrintFileId };
        onPrinter.TransferStartedAt = _timeProvider.GetUtcNow();

        if (onPrinter.Id == 0)
        {
            dbContext.PrintFilesOnPrinters.Add(onPrinter);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            CommandOutcome? outcome = await sender.SendAsync(printer, file, head.QueuedByUserId, cancellationToken);

            if (outcome?.EventType is Events.Rejected or Events.Failed)
            {
                // Almost always the single system-wide transfer slot being busy. Clearing the stamp
                // makes the next tick try again rather than waiting out the staleness timeout.
                _logger.LogInformation("[{PrinterId}] refused the transfer of {FileName}: {Reason}",
                    printerId, file.FileName, outcome.Reason);
                onPrinter.TransferStartedAt = null;
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException
            or CommandResponseTimedOutException or CommandSendTimedOutException or PrintFileUnreadableException)
        {
            _logger.LogInformation(e, "[{PrinterId}] could not start the transfer of {FileName}",
                printerId, file.FileName);
            onPrinter.TransferStartedAt = null;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (TeamAccessDeniedException)
        {
            // Whoever queued this may no longer use the printer. Leaving the entry in place is
            // deliberate - it is not this loop's business to cancel somebody's print because their
            // permissions changed, and a restored permission resumes it.
            _logger.LogWarning("[{PrinterId}] {FileName} is queued by a user who may no longer use this printer",
                printerId, file.FileName);
            onPrinter.TransferStartedAt = null;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Starts the print, and removes the entry once the printer has taken it.</summary>
    /// <remarks>
    /// <b>Success is not <c>FINISHED</c>.</b> A <c>START_PRINT</c> that took answers <c>JOB_INFO</c>
    /// (planner.cpp:728), so this tests for the absence of a refusal rather than for a particular
    /// event - the check that would otherwise read a started print as an unrecognised answer.
    /// </remarks>
    private async Task PrintAsync(AsyncServiceScope scope, HSDbContext dbContext, int printerId,
        QueuedPrint head, string printerPath, CancellationToken cancellationToken)
    {
        PrinterCommandService commands = scope.ServiceProvider.GetRequiredService<PrinterCommandService>();

        try
        {
            CommandOutcome? outcome = await commands.SendCommandAsync(printerId,
                new StartPrint { Path = printerPath }, head.QueuedByUserId, cancellationToken);

            if (outcome?.EventType is Events.Rejected or Events.Failed)
            {
                HandleRefusal(printerId, dbContext, head, outcome.Reason);
                await dbContext.SaveChangesAsync(cancellationToken);

                return;
            }

            _logger.LogInformation("[{PrinterId}] started printing {Path}", printerId, printerPath);

            // The entry has done its job. Print history, when it exists, is what will remember this
            // happened - notes/print-queue.md, "Print history, not jobs".
            dbContext.QueuedPrints.Remove(head);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException
            or CommandResponseTimedOutException or CommandSendTimedOutException or TeamAccessDeniedException)
        {
            // Transient by nature: the next tick asks again.
            _logger.LogInformation(e, "[{PrinterId}] could not start {Path}", printerId, printerPath);
        }
    }

    /// <summary>
    /// Applies the retry rules to a refused <c>START_PRINT</c> - firmware's own reason string decides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only four reasons are reachable and <b>only one is transient</b>
    /// (<c>notes/print-queue.md</c>, corrected 2026-08-02): <c>Can't print now</c> is a wrong state or
    /// a <c>print_begin</c> that did not take, and waiting is the whole response.
    /// <c>Forbidden path</c> and <c>Tools mapping not enabled</c> are terminal, and retrying either
    /// would hide a misconfiguration behind a queue that looks merely slow.
    /// </para>
    /// <para>
    /// <c>File not found</c> is the interesting one: it is the drive correcting us. The bytes were
    /// believed present and are not - deleted at the panel, or a card swapped - so the belief is
    /// cleared and the file is offered again rather than the entry being failed.
    /// </para>
    /// </remarks>
    private void HandleRefusal(int printerId, HSDbContext dbContext, QueuedPrint head, string? reason)
    {
        switch (reason)
        {
            case "File not found":
                _logger.LogInformation("[{PrinterId}] the drive no longer has {FileName}; sending it again",
                    printerId, head.PrintFile?.Name);

                dbContext.PrintFilesOnPrinters
                         .Where(row => row.PrinterId == printerId && row.PrintFileId == head.PrintFileId)
                         .ExecuteDelete();
                break;

            case "Forbidden path":
            case "Tools mapping not enabled":
                _logger.LogError(
                    "[{PrinterId}] refused {FileName} with \"{Reason}\", which will not change by retrying; "
                    + "removing it from the queue.",
                    printerId, head.PrintFile?.Name, reason);

                dbContext.QueuedPrints.Remove(head);
                break;

            default:
                // "Can't print now", and anything a future firmware adds. Waiting is free and the next
                // tick asks again; treating an unrecognised reason as terminal would throw away a
                // print for a string nobody has read yet.
                _logger.LogDebug("[{PrinterId}] not printing yet: {Reason}", printerId, reason);
                break;
        }
    }
}
