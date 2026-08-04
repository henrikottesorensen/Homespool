using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
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
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Queue;

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

    /// <summary>
    /// How long a print may sit commanded-but-not-printing before the loop stops believing in it.
    /// </summary>
    /// <remarks>
    /// The bound the <see cref="PrintOutcome.Starting"/> phase needs. Ordinarily this is seconds -
    /// 3.1 s measured on a Core One - but a print that is accepted and then never begins (a heat-up
    /// that fails, a dialog nobody answers) would otherwise leave the row open forever, and the
    /// partial unique index would then block every later print on that printer. Generous, because a
    /// cold chamber and a large bed legitimately take minutes; closing it as
    /// <see cref="PrintOutcome.Unknown"/> is honest, since nothing here can say what happened.
    /// </remarks>
    public static readonly TimeSpan StartingStaleAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How often a held queue re-asks whether there is room now.
    /// </summary>
    /// <remarks>
    /// A block clears by itself - somebody deletes files at the panel and the queue resumes without
    /// anyone pressing anything - so the loop has to keep looking. This only stops that costing a
    /// command every tick for as long as the block lasts.
    /// </remarks>
    public static readonly TimeSpan BlockRecheckAfter = TimeSpan.FromSeconds(60);

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

    /// <summary>One pass over every printer that needs one. Public so a test can drive it.</summary>
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

            printerIds = await PrintersNeedingAPassAsync(dbContext, cancellationToken);
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

    /// <summary>
    /// Every printer that needs a pass - one with work waiting, or one with a print in flight.
    /// </summary>
    /// <remarks>
    /// <b>Two conditions because the pass does two jobs.</b> It advances the queue and it reconciles
    /// the open print, and until 2026-08-04 it was scheduled by only the first - so a printer went
    /// unvisited from the moment its last queue entry was consumed at <c>START_PRINT</c>, which is
    /// exactly when its print row still needed closing. The last print of a session never closed, and
    /// a row stuck <see cref="PrintOutcome.Starting"/> blocked the next print for
    /// <see cref="StartingStaleAfter"/>. The predicate predated print history by a day and nobody
    /// revisited it when <see cref="ReconcilePrintAsync"/> moved in.
    /// <para>
    /// <c>Union</c> dedupes in SQL, and the second arm is served by the same partial unique index
    /// (<c>PrinterId WHERE EndedAt IS NULL</c>) that enforces one active print per printer. Still
    /// self-limiting: the row closes, the queue is empty, the printer drops off the list.
    /// </para>
    /// </remarks>
    private static Task<List<int>> PrintersNeedingAPassAsync(HSDbContext dbContext,
        CancellationToken cancellationToken)
    {
        return dbContext.QueuedPrints
                        .Select(queued => queued.PrinterId)
                        .Union(dbContext.PrintJobs
                                        .Where(job => job.EndedAt == null)
                                        .Select(job => job.PrinterId))
                        .ToListAsync(cancellationToken);
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

    /// <summary>Ends a print row: the outcome and the moment, together so neither is set alone.</summary>
    private static void Close(PrintJob job, PrintOutcome outcome, DateTimeOffset at)
    {
        job.Outcome = outcome;
        job.EndedAt = at;
    }

    private async Task AdvanceOnceAsync(int printerId, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        HSDbContext dbContext = scope.ServiceProvider.GetRequiredService<HSDbContext>();

        // Read the printer's own reports first, so a transfer that finished since the last pass is
        // known before anything is decided on the assumption that it has not.
        await ReconcileArrivalsAsync(dbContext, printerId, cancellationToken);

        PrinterLiveState? live = await dbContext.PrinterLiveStates
                                                .AsNoTracking()
                                                .SingleOrDefaultAsync(state => state.PrinterId == printerId,
                                                    cancellationToken);

        await ReconcilePrintAsync(dbContext, printerId, live, cancellationToken);

        // Asked of the shared reader rather than assembled here, so that anything explaining the loop
        // to a person is answering the same question the loop asked. Two builders would agree on the
        // day they were written and drift afterwards.
        QueueSnapshot snapshot = await scope.ServiceProvider
                                            .GetRequiredService<QueueSnapshotReader>()
                                            .ReadAsync(printerId, cancellationToken);

        if (snapshot.Head is null)
        {
            return;
        }

        // Tracked, unlike the reader's copy, because this one may be removed or have its file sent.
        QueuedPrint head = await dbContext.QueuedPrints
                                          .Include(queued => queued.PrintFile)
                                          .SingleAsync(queued => queued.Id == snapshot.Head.QueuedPrintId,
                                              cancellationToken);

        PrintFileOnPrinter? onPrinter = await dbContext.PrintFilesOnPrinters
            .SingleOrDefaultAsync(row => row.PrinterId == printerId && row.PrintFileId == head.PrintFileId,
                cancellationToken);

        QueueAction action = QueueRules.Decide(snapshot);

        switch (action.Kind)
        {
            case QueueActionKind.Transfer:
                await TransferAsync(scope, dbContext, printerId, head, onPrinter, cancellationToken);
                break;

            case QueueActionKind.Print:
                await PrintAsync(scope, dbContext, printerId, head, action.Head!.PrinterPath!, cancellationToken);
                break;

            case QueueActionKind.Wait when action.Reason == QueueWaitReason.InsufficientSpace:
                // Routed into the transfer path rather than merely logged, because that path is what
                // *re-checks* the drive and clears the block - it begins by asking, and only then
                // sends. Teaching the rules about the block (so a page could not report a transfer
                // that cannot happen) made this necessary: without it the block is self-perpetuating,
                // the rules refusing to transfer and nothing left able to discover there is room now.
                // Caught by the end-to-end test that frees space and expects the queue to resume.
                await TransferAsync(scope, dbContext, printerId, head, onPrinter, cancellationToken);
                break;

            case QueueActionKind.Wait:
                _logger.LogDebug("[{PrinterId}] queue holding: {Reason}", printerId, action.Reason);
                break;

            default:
                break;
        }
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

    /// <summary>
    /// Moves this printer's open print through its two phases, and closes it when the printer stops.
    /// </summary>
    /// <returns>The still-open print, or null if there is none.</returns>
    /// <remarks>
    /// <para>
    /// <b>Two phases, because a print does not begin when it is commanded.</b> A row is opened
    /// <see cref="PrintOutcome.Starting"/> and only reaches <see cref="PrintOutcome.Printing"/> when
    /// telemetry actually says so - measured at 3.1 s on a Core One, which still reports <c>READY</c>
    /// throughout. Closing on "no longer printing" without that distinction would close every print
    /// moments after starting it, and the FakePrinter would never have shown it: the fake transitions
    /// instantly, where firmware passes through preview-init and heating.
    /// </para>
    /// <para>
    /// <b>The firmware job id is taken from telemetry rather than the ack.</b> <c>START_PRINT</c>
    /// answers <c>JOB_INFO</c> carrying it, but <c>SendCommandAsync</c> returns a verdict rather than
    /// a payload - and telemetry repeats <c>job_id</c> for the whole print, so reading it here needs
    /// no second command and survives a restart, which is the point of keeping the mapping at all.
    /// </para>
    /// <para>
    /// <b>Paused and Attention are not endings.</b> They are stalls inside a print, and the loop waits
    /// them out rather than deciding anything - "don't cancel prints on people".
    /// </para>
    /// </remarks>
    private async Task<PrintJob?> ReconcilePrintAsync(HSDbContext dbContext, int printerId,
        PrinterLiveState? live, CancellationToken cancellationToken)
    {
        PrintJob? active = await dbContext.PrintJobs
            .SingleOrDefaultAsync(job => job.PrinterId == printerId && job.EndedAt == null, cancellationToken);

        if (active is null)
        {
            return null;
        }

        PrinterStatus status = live?.Status ?? PrinterStatus.Unknown;
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (active.Outcome == PrintOutcome.Starting)
        {
            if (status == PrinterStatus.Printing)
            {
                active.Outcome = PrintOutcome.Printing;
                active.FirmwareJobId = live?.JobId;
                await dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("[{PrinterId}] {FileName} is printing (firmware job {JobId})",
                    printerId, active.FileName, active.FirmwareJobId);

                return active;
            }

            if (now - active.StartedAt < StartingStaleAfter)
            {
                return active;
            }

            // Accepted and never begun. Closing it as Unknown is honest - nothing here can say why -
            // and it is what stops the partial unique index blocking this printer forever.
            _logger.LogWarning(
                "[{PrinterId}] {FileName} was accepted {Elapsed:F0} minutes ago and never started printing; "
                + "closing it as Unknown so the queue is not wedged.",
                printerId, active.FileName, (now - active.StartedAt).TotalMinutes);

            Close(active, PrintOutcome.Unknown, now);
            await dbContext.SaveChangesAsync(cancellationToken);

            return null;
        }

        if (status is PrinterStatus.Printing or PrinterStatus.Paused or PrinterStatus.Attention)
        {
            return active;
        }

        PrintOutcome outcome = status switch
        {
            PrinterStatus.Finished => PrintOutcome.Finished,
            PrinterStatus.Stopped => PrintOutcome.Stopped,

            // Idle, Ready, Error, or the printer having gone quiet. It stopped printing and did not
            // say how, which is what Unknown is for rather than a guess at Finished.
            _ => PrintOutcome.Unknown,
        };

        Close(active, outcome, now);
        await dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("[{PrinterId}] {FileName} ended: {Outcome}", printerId, active.FileName, outcome);

        return null;
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

        onPrinter ??= new PrintFileOnPrinter { PrinterId = printerId, PrintFileId = head.PrintFileId };

        if (onPrinter.Id == 0)
        {
            dbContext.PrintFilesOnPrinters.Add(onPrinter);
        }

        if (!await HasRoomForAsync(scope, dbContext, printerId, head, file.Length, onPrinter, cancellationToken))
        {
            return;
        }

        PrintFileSender sender = scope.ServiceProvider.GetRequiredService<PrintFileSender>();

        // Recorded before the send rather than after: the printer can begin asking for chunks the
        // instant it accepts, and a row written afterwards would leave a window in which the next tick
        // saw no transfer and offered the file again.
        onPrinter.TransferStartedAt = _timeProvider.GetUtcNow();
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

    /// <summary>
    /// Asks the printer whether the file will fit, and holds the queue if it will not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asked rather than remembered.</b> Free space is only on the wire in <c>INFO</c>'s storage
    /// block, and an unsolicited <c>INFO</c> arrives on connect and when <c>info_fingerprint()</c>
    /// changes - which free space is not part of. A figure kept from connect time therefore goes stale
    /// exactly as a queue fills the drive, which is the one situation it exists to catch. So the loop
    /// asks, and a held queue re-asks at <see cref="BlockRecheckAfter"/> rather than every tick.
    /// </para>
    /// <para>
    /// <b>The queue holds behind a file that does not fit</b> (Henrik: *"Holds, like a traditional
    /// printer spooler"*). Not skipped - a shared queue whose order silently rearranges is worse than
    /// one that visibly stops - and not cancelled, because the condition is recoverable by a person
    /// deleting files, and it clears itself when they do. The failed attempt is written to print
    /// history <b>once</b>, carrying both numbers, so there is something to read rather than a queue
    /// that merely stopped.
    /// </para>
    /// <para>
    /// <b>Unknown space is treated as room.</b> A printer that reports no <c>storages</c> block tells
    /// us nothing, and refusing to print on a measurement we do not have would be worse than trying:
    /// a genuinely full drive fails the transfer loudly, where a wrong refusal is silent.
    /// </para>
    /// </remarks>
    private async Task<bool> HasRoomForAsync(AsyncServiceScope scope, HSDbContext dbContext, int printerId,
        QueuedPrint head, long length, PrintFileOnPrinter onPrinter, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (onPrinter.BlockedReason is not null
            && onPrinter.BlockedAt is { } blockedAt
            && now - blockedAt < BlockRecheckAfter)
        {
            // Still held, and asked recently enough. Saying nothing here is deliberate: a held queue
            // that logged every tick would bury the one line that explains it.
            return false;
        }

        PrinterCommandService commands = scope.ServiceProvider.GetRequiredService<PrinterCommandService>();
        long? free;

        try
        {
            CommandOutcome<InfoEventDataDTO>? answer =
                await commands.AskAsync(printerId, new SendInfo(), head.QueuedByUserId, cancellationToken);

            free = answer?.Answer?.Storages?
                         .FirstOrDefault(storage => storage.MountPoint == "/usb")?
                         .FreeSpace;
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException
            or CommandResponseTimedOutException or CommandSendTimedOutException or TeamAccessDeniedException
            or CommandAnswerUnreadableException)
        {
            // Could not ask. Not a block - the next pass asks again, and treating an unanswered
            // question as "no room" would hold a queue on a printer that was merely busy.
            _logger.LogDebug(e, "[{PrinterId}] could not ask about free space", printerId);

            return false;
        }

        if (free is null || free >= length)
        {
            if (onPrinter.BlockedReason is not null)
            {
                _logger.LogInformation("[{PrinterId}] there is room for {FileName} now; the queue resumes",
                    printerId, head.PrintFile!.Name);

                onPrinter.BlockedReason = null;
                onPrinter.BlockedAt = null;
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return true;
        }

        string reason = string.Create(CultureInfo.InvariantCulture,
            $"Not enough space on the printer: {head.PrintFile!.Name} needs {length} bytes, {free} free.");

        bool newlyBlocked = onPrinter.BlockedReason is null;

        onPrinter.BlockedReason = reason;
        onPrinter.BlockedAt = now;

        if (newlyBlocked)
        {
            // Written once, on the transition. A row per tick would turn history into a log, and the
            // queue entry itself stays put - somebody still wants this printed.
            dbContext.PrintJobs.Add(new PrintJob
            {
                PrinterId = printerId,
                TrackingId = head.TrackingId,
                FileName = head.PrintFile.Name,
                Digest = head.PrintFile.Digest,
                QueuedByUserId = head.QueuedByUserId,
                StartedAt = now,
                EndedAt = now,
                Outcome = PrintOutcome.Failed,
                Reason = reason,
            });

            _logger.LogWarning("[{PrinterId}] {Reason} The queue holds until space is freed.", printerId, reason);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return false;
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

            // Opened Starting rather than Printing: the printer has accepted the command and will keep
            // reporting READY for a few seconds yet. ReconcilePrintAsync promotes it when telemetry
            // says otherwise, and that is also where the firmware job id is picked up.
            dbContext.PrintJobs.Add(new PrintJob
            {
                PrinterId = printerId,
                TrackingId = head.TrackingId,
                FileName = head.PrintFile!.Name,
                Digest = head.PrintFile.Digest,
                QueuedByUserId = head.QueuedByUserId,
                PrinterPath = printerPath,
                StartedAt = _timeProvider.GetUtcNow(),
                Outcome = PrintOutcome.Starting,
            });

            // The entry has done its job; the history row carries it from here.
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

                // Recorded as a failed print rather than only logged. Dropping the entry with nothing
                // to show for it is how a queued print used to vanish with no way for its owner to
                // find out why. Opened and closed in the same moment, which is honest: nothing printed.
                DateTimeOffset refusedAt = _timeProvider.GetUtcNow();

                dbContext.PrintJobs.Add(new PrintJob
                {
                    PrinterId = printerId,
                    TrackingId = head.TrackingId,
                    FileName = head.PrintFile?.Name ?? string.Empty,
                    Digest = head.PrintFile?.Digest,
                    QueuedByUserId = head.QueuedByUserId,
                    StartedAt = refusedAt,
                    EndedAt = refusedAt,
                    Outcome = PrintOutcome.Failed,
                    Reason = reason,
                });

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
