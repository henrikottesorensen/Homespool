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
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Queue;

/// <summary>
/// The producer loop: for each printer, work out what its queue needs next and do it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A hosted service rather than anything inside <c>PrinterConnectionActor</c>.</b>
/// The actor has no database access by design - that is what keeps its
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
/// between passes beyond the event watermark and the last panel job examined, both optimisations
/// rather than state: losing either costs a re-scan or a repeated question, not correctness. A
/// restart therefore resumes without ceremony, and the design's "nudged on enqueue and on connect"
/// is a latency improvement over the timer rather than the mechanism.
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
    /// <para>
    /// <b>A backstop for cases nobody enumerated, not the mechanism.</b> The reconciler decides a
    /// <see cref="PrintState.Starting"/> row on evidence - it promotes on <c>PRINTING</c>, closes on
    /// a stated <c>FINISHED</c>/<c>STOPPED</c>, and closes on a job id it once held being withdrawn
    /// by an idle printer. This only catches whatever none of those saw.
    /// </para>
    /// <para>
    /// <b>Being generous is therefore cheap, and being tight is not.</b> The one thing legitimately
    /// waiting here is a preview dialog still carrying our job id, which the person at the machine
    /// can answer at any time - and the queue entry is consumed at the ack, so closing that row
    /// early would let them press Print on a job with no row and no entry left to adopt it against.
    /// A print running that nothing here has a record of is far worse than a wait.
    /// </para>
    /// <para>
    /// The phase itself is seconds: 1.0-7 s measured across a Core One and an MK3.5, the latter
    /// reporting <c>PRINTING</c> in the first sample after <c>START_PRINT</c>. Minutes of cold
    /// chamber and cold bed do not happen here - a print's start gcode runs *inside*
    /// <c>State::Printing</c>, so all of that heating is on the far side of the promotion.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan StartingStaleAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a printer may report itself not printing before that means it ignored a
    /// <c>START_PRINT</c> rather than that it has not got round to it.
    /// </summary>
    /// <remarks>
    /// <b>The window exists because acceptance is not instant.</b> A Core One keeps reporting
    /// <c>READY</c> for 3.1 s after taking a print, while it works through preview-init and heating;
    /// an MK3.5 reports <c>PRINTING</c> in the first sample. Reading a not-printing status inside
    /// that gap as "the command was ignored" would drop a print that was starting perfectly well.
    /// A minute is far more than any measurement here and costs nothing but a minute in the case
    /// where the command genuinely did not land - which is the rare half of a rare event.
    /// </remarks>
    public static readonly TimeSpan StartUnconfirmedGrace = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the loop keeps asking a connected printer what it is printing before it gives up and
    /// holds the queue instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reached only by a printer that is connected, reports a job, and will not describe it</b> -
    /// answering telemetry while refusing commands, for a quarter of an hour. That is a machine in
    /// trouble rather than a machine that is busy.
    /// </para>
    /// <para>
    /// <b>There is a bound at all because waiting for days on a connected printer is not an answer</b>
    /// (Henrik, 2026-08-22). What it must not do is guess: advancing could print the file a second
    /// time, which is the whole defect, so the give-up is a hold with a sentence rather than a
    /// decision. See <see cref="PrintHoldReason.PrintStartUnresolved"/>.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan StartUnresolvableAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How often a held queue re-asks whether there is room now.
    /// </summary>
    /// <remarks>
    /// A block clears by itself - somebody deletes files at the panel and the queue resumes without
    /// anyone pressing anything - so the loop has to keep looking. This only stops that costing a
    /// command every tick for as long as the block lasts.
    /// </remarks>
    public static readonly TimeSpan BlockRecheckAfter = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Firmware's code for "the drive already has that name" - observed on an MK3.5 at 6.5.7+12836,
    /// answering <c>START_CONNECT_DOWNLOAD</c> with <c>"File already exists"</c> beside it.
    /// </summary>
    /// <remarks>
    /// Its own code rather than a <c>STORAGE_FAILURE</c>, which is what makes this case separable:
    /// firmware distinguishes "it is already there" from "storage went wrong", so the loop can too.
    /// </remarks>
    private const string FileExistsCode = "FILE_EXISTS";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PrinterConnectionRegistry _registry;
    private readonly QueueSignal _signal;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<QueueAdvancer> _logger;

    /// <summary>Last <c>PrinterEvent</c> id examined per printer - see the class remarks.</summary>
    private readonly Dictionary<int, long> _watermarks = [];

    /// <summary>
    /// The last firmware job id each printer was asked about by
    /// <see cref="TryAdoptPanelPrintAsync"/> and found not to be ours - so a stranger's print costs
    /// one question, not one per pass.
    /// </summary>
    /// <remarks>
    /// Like <see cref="_watermarks"/>, memory as an optimisation rather than state: losing it - a
    /// restart - costs one repeated <c>SEND_JOB_INFO</c>, not correctness.
    /// </remarks>
    private readonly Dictionary<int, int> _examinedPanelJobs = [];

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
            HomespoolDbContext dbContext = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

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
    /// a row stuck <see cref="PrintState.Starting"/> blocked the next print for
    /// <see cref="StartingStaleAfter"/>. The predicate predated print history by a day and nobody
    /// revisited it when <see cref="ReconcilePrintAsync"/> moved in.
    /// <para>
    /// <c>Union</c> dedupes in SQL, and the second arm is served by the same partial unique index
    /// (<c>PrinterId WHERE EndedAt IS NULL</c>) that enforces one active print per printer. Still
    /// self-limiting: the row closes, the queue is empty, the printer drops off the list.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The authority a queue entry was accepted under - <b>not merely the person who queued it</b>.
    /// </summary>
    /// <remarks>
    /// The loop has no credential of its own, so it acts on the one recorded when the work was
    /// accepted. Acting as the user alone would run their work with more authority than the token that
    /// queued it, which is privilege escalation across a time boundary: the membership half is
    /// re-checked at send time, and this is what re-checks the credential half beside it.
    /// </remarks>
    private static Caller CallerFor(QueuedPrint head)
    {
        return Caller.Scoped(head.QueuedByUserId, CapabilitySet.Parse(head.QueuedByScope));
    }

    /// <summary>
    /// Lifts a hold, and clears what was recorded about it.
    /// </summary>
    /// <remarks>
    /// One place, because a hold is now four fields rather than one and leaving a stale byte count
    /// behind a cleared reason would put a number on a page that describes nothing.
    /// </remarks>
    private static void ClearHold(PrintFileOnPrinter onPrinter)
    {
        onPrinter.HoldReason = null;
        onPrinter.HoldPrinterFreeBytes = null;
        onPrinter.HoldPrinterFileBytes = null;
        onPrinter.BlockedAt = null;
    }

    private static Task<List<int>> PrintersNeedingAPassAsync(HomespoolDbContext dbContext,
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
    private static void Close(PrintJob job, PrintState outcome, DateTimeOffset at)
    {
        job.State = outcome;
        job.EndedAt = at;
    }

    private async Task AdvanceOnceAsync(int printerId, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        HomespoolDbContext dbContext = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        // Read the printer's own reports first, so a transfer that finished since the last pass is
        // known before anything is decided on the assumption that it has not.
        await ReconcileArrivalsAsync(dbContext, printerId, cancellationToken);

        PrinterLiveState? live = await dbContext.PrinterLiveStates
                                                .AsNoTracking()
                                                .SingleOrDefaultAsync(state => state.PrinterId == printerId,
                                                                      cancellationToken);

        await ReconcilePrintAsync(scope, dbContext, printerId, live, cancellationToken);

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
                                                       .SingleOrDefaultAsync(
                                                           row => row.PrinterId == printerId && row.PrintFileId == head.PrintFileId,
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
    private async Task ReconcileArrivalsAsync(HomespoolDbContext dbContext,
                                              int printerId,
                                              CancellationToken cancellationToken)
    {
        long watermark = _watermarks.TryGetValue(printerId, out long last) ? last : 0;

        List<PrinterEvent> events = await dbContext.PrinterEvents
                                                   .AsNoTracking()
                                                   .Where(printerEvent => printerEvent.PrinterId == printerId
                                                                          && printerEvent.Id > watermark
                                                                          && printerEvent.EventType == PrinterEventType.FileInfo)
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
    /// <see cref="PrintState.Starting"/> and only reaches <see cref="PrintState.Printing"/> when
    /// telemetry actually says so - measured at 3.1 s on a Core One, which still reports <c>READY</c>
    /// throughout, and at <b>zero</b> on an MK3.5, whose very first sample already says <c>PRINTING</c>
    /// with the nozzle cold (hardware, 2026-08-04). The window is not something every printer offers;
    /// the guard is what earns the two phases, not the gap
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
    /// <para>
    /// <b>A third phase comes before both</b>, and it is not part of an ordinary print's life:
    /// <see cref="PrintState.Unconfirmed"/>, where the command went out and nothing came back. That
    /// one cannot be moved on by watching, because watching cannot tell <i>whose</i> print a printer
    /// is running - so it is settled by asking. See <see cref="ResolveUnconfirmedPrintAsync"/>.
    /// </para>
    /// </remarks>
    private async Task<PrintJob?> ReconcilePrintAsync(AsyncServiceScope scope,
                                                      HomespoolDbContext dbContext,
                                                      int printerId,
                                                      PrinterLiveState? live,
                                                      CancellationToken cancellationToken)
    {
        PrintJob? active = await dbContext.PrintJobs
                                          .SingleOrDefaultAsync(job => job.PrinterId == printerId && job.EndedAt == null,
                                                                cancellationToken);

        if (active is null)
        {
            return await TryAdoptPanelPrintAsync(scope, dbContext, printerId, live, cancellationToken);
        }

        PrinterStatus status = live?.Status ?? PrinterStatus.Unknown;
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (active.State == PrintState.Unconfirmed)
        {
            PrintStartVerdict verdict =
                await ResolveUnconfirmedPrintAsync(scope, dbContext, printerId, active, live, cancellationToken);

            if (verdict != PrintStartVerdict.Started)
            {
                // Still a question, or no longer a print. Either way there is nothing here for the
                // two ordinary phases to act on - and a row still being asked about must keep its
                // open slot, so the rules go on seeing a print in flight and hold the queue.
                return verdict == PrintStartVerdict.KeepWaiting ? active : null;
            }

            // Adopted. It is an ordinary Starting row now, so the rest of this method treats it as
            // one - which promotes it to Printing in this same pass, since the telemetry that
            // identified it is the telemetry that says the printer is printing.
        }

        if (active.State == PrintState.Starting)
        {
            // The job id is the evidence, and it arrives whether or not Printing is ever sampled.
            // Firmware assigns one the moment it accepts, and keeps reporting it through a preview
            // dialog - so recording it here is what lets a later withdrawal mean something. Ours by
            // construction: a printer already running somebody else's job refuses START_PRINT, so
            // the only job it can be reporting seconds after accepting ours is ours.
            if (live?.JobId is { } offered && active.FirmwareJobId is null)
            {
                active.FirmwareJobId = offered;
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            if (status == PrinterStatus.Printing)
            {
                active.State = PrintState.Printing;
                active.FirmwareJobId = live?.JobId ?? active.FirmwareJobId;
                await dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("[{PrinterId}] {FileName} is printing (firmware job {JobId})",
                                       printerId, active.FileName, active.FirmwareJobId);

                return active;
            }

            // Said plainly by the printer, so there is nothing to wait out.
            if (status is PrinterStatus.Finished or PrinterStatus.Stopped)
            {
                PrintState said = status == PrinterStatus.Finished ? PrintState.Finished : PrintState.Stopped;

                Close(active, said, now);
                await dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("[{PrinterId}] {FileName} ended before it began: {Outcome}",
                                       printerId, active.FileName, said);

                return null;
            }

            // Taken up, and now withdrawn: we hold a job id, the printer reports none, and it is not
            // in any state that could still be starting. Whatever ended it - a stop of ours, an
            // Abort at the panel, a refusal we never saw - it is over, and this is the only signal a
            // panel abort gives us. It sends no event at all, unlike our own STOP_PRINT, which is
            // why closing on the job id rather than on an event covers both.
            //
            // The FirmwareJobId guard is what keeps a legitimate start alive: firmware reports
            // Idle or Ready through PrintInit while it opens the file, and carries no job id yet -
            // so a row that has never seen one is still starting, not finished.
            if (active.FirmwareJobId is not null &&
                live?.JobId is null &&
                status is PrinterStatus.Idle or PrinterStatus.Ready or PrinterStatus.Error)
            {
                _logger.LogInformation("[{PrinterId}] {FileName} was accepted as firmware job {JobId} and never began; the printer " +
                                       "is {Status} and reports no job, so it is over.",
                                       printerId, active.FileName, active.FirmwareJobId, status);

                Close(active, PrintState.Unknown, now);
                await dbContext.SaveChangesAsync(cancellationToken);

                return null;
            }

            // Everything reaching here is a wait this pass can name: a dialog or a stall still
            // carrying our job id, where the person at the machine can yet answer it and the row
            // must survive to be promoted; or the seconds before the printer reports anything at
            // all. Nothing falls through by not matching - which is what let Idle and Attention
            // alike sit here for the whole of StartingStaleAfter.
            if (now - active.StartedAt < StartingStaleAfter)
            {
                _logger.LogDebug("[{PrinterId}] {FileName} is still starting: printer is {Status}, job {JobId}",
                                 printerId, active.FileName, status, live?.JobId);

                return active;
            }

            // The backstop, and only the backstop: a case nobody enumerated above. The row closes
            // either way - that is what stops the partial unique index blocking this printer forever
            // - so the only question left is whether it closes on a guess. Ask first: the printer
            // keeps the outcome of its last two jobs and this row is very likely one of them.
            PrintState settled = await AskPriorOutcomeAsync(scope, printerId, active, cancellationToken)
                                 ?? PrintState.Unknown;

            _logger.LogWarning("[{PrinterId}] {FileName} was accepted {Elapsed:F0} minutes ago and never started " +
                               "printing; closing it as {Outcome} so the queue is not wedged.",
                               printerId, active.FileName, (now - active.StartedAt).TotalMinutes, settled);

            Close(active, settled, now);
            await dbContext.SaveChangesAsync(cancellationToken);

            return null;
        }

        // Busy belongs in this stall set on hardware evidence: a filament runout opens with
        // several seconds of BUSY carrying no job id before it settles into ATTENTION (MK3.5,
        // observed live 2026-08-28), and reading that excursion as an ending closed a row mid-print
        // while the printer went on to finish the file.
        if (status is PrinterStatus.Printing or PrinterStatus.Paused or PrinterStatus.Attention or PrinterStatus.Busy)
        {
            return active;
        }

        PrintState outcome = status switch
        {
            PrinterStatus.Finished => PrintState.Finished,
            PrinterStatus.Stopped => PrintState.Stopped,

            // Idle, Ready, Error, or the printer having gone quiet. It stopped printing and did not
            // say how, which is what Unknown is for rather than a guess at Finished.
            _ => PrintState.Unknown,
        };

        Close(active, outcome, now);
        await dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("[{PrinterId}] {FileName} ended: {Outcome}", printerId, active.FileName, outcome);

        return null;
    }

    /// <summary>
    /// Attributes a print the printer started by itself, when it is one of ours by construction:
    /// a running job whose reported path is one this loop wrote, for a file that is still queued.
    /// </summary>
    /// <returns>The adopted row, or null when there is nothing to adopt.</returns>
    /// <remarks>
    /// <para>
    /// <b>A staged file is also the panel's offer.</b> Firmware opens its one-click print preview
    /// for a file that arrives over the wire, so the person at the machine is offered exactly the
    /// file this loop was about to command - and they are behind a button while the loop is behind
    /// a poll, so when both want the same print, the panel wins. Such a print is indistinguishable
    /// in telemetry from any other panel print, and without this it left no history row while its
    /// queue entry survived to print the file a second time.
    /// </para>
    /// <para>
    /// <b>Adopted on the path, never on the status.</b> "The printer is printing and something is
    /// queued" would attach somebody's queue entry to a stranger's print and then delete the entry.
    /// The <c>JOB_INFO</c> answer naming the path recorded at transfer time, for an entry still in
    /// the queue, is a different claim: ours by construction rather than by inference. A running
    /// print that matches nothing stays unattributed, and the queue holds on the printer not being
    /// <c>Ready</c>, as it always has.
    /// </para>
    /// <para>
    /// <b>Only from <c>Printing</c> or <c>Paused</c>.</b> A job id is already on the wire during
    /// the preview's own questions (<c>ATTENTION</c>), while the person can still back out -
    /// adopting there would consume the entry for a print that never runs. A print that stalls into
    /// attention later is adopted once it resumes.
    /// </para>
    /// </remarks>
    private async Task<PrintJob?> TryAdoptPanelPrintAsync(AsyncServiceScope scope,
                                                          HomespoolDbContext dbContext,
                                                          int printerId,
                                                          PrinterLiveState? live,
                                                          CancellationToken cancellationToken)
    {
        if (live?.JobId is not { } jobId ||
            live.Status is not (PrinterStatus.Printing or PrinterStatus.Paused) ||
            !_registry.IsConnected(printerId))
        {
            return null;
        }

        if (_examinedPanelJobs.TryGetValue(printerId, out int examined) && examined == jobId)
        {
            return null;
        }

        // The candidates are queued entries whose file this loop put on the drive, in queue order.
        // The recorded path is the thing adoption matches on, so an entry without one cannot be
        // claimed - and with no candidates at all the running print cannot be ours, and there is
        // nothing to ask.
        var candidates = await dbContext.QueuedPrints
                                        .Include(queued => queued.PrintFile)
                                        .Join(dbContext.PrintFilesOnPrinters,
                                              queued => new { queued.PrinterId, queued.PrintFileId },
                                              onPrinter => new { onPrinter.PrinterId, onPrinter.PrintFileId },
                                              (queued, onPrinter) => new { Entry = queued, onPrinter.PrinterPath })
                                        .Where(candidate => candidate.Entry.PrinterId == printerId
                                                            && candidate.PrinterPath != null)
                                        .OrderBy(candidate => candidate.Entry.Position)
                                        .ThenBy(candidate => candidate.Entry.Id)
                                        .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            _examinedPanelJobs[printerId] = jobId;
            return null;
        }

        PrinterCommandService commands = scope.ServiceProvider.GetRequiredService<PrinterCommandService>();
        CommandOutcome<JobInfoEventDataDTO>? answer;

        try
        {
            answer = await commands.AskAsync(printerId,
                                             new PrusaConnect.Commands.SendJobInfo { JobId = jobId },
                                             CallerFor(candidates[0].Entry),
                                             cancellationToken);
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException
                                      or CommandResponseTimedOutException or CommandSendTimedOutException or
                                      TeamAccessDeniedException or CredentialScopeDeniedException
                                      or CommandAnswerUnreadableException)
        {
            _logger.LogDebug(e, "[{PrinterId}] could not ask about firmware job {JobId}", printerId, jobId);

            return null;
        }

        if (answer?.EventType is PrinterEventType.Rejected or PrinterEventType.Failed)
        {
            // "No job in progress" while telemetry reports one is the start window - the job may be
            // about to become describable, so it is asked about again rather than written off.
            // Anything else is an answer: the printer will not name this job, and asking every pass
            // for the rest of a stranger's print would not change that.
            if (answer.Reason != "No job in progress")
            {
                _examinedPanelJobs[printerId] = jobId;
            }

            return null;
        }

        if (answer?.Answer is not { } job || (job.Path is null && job.DisplayName is null))
        {
            // A job described without a name settles nothing - and a *current* job should always
            // carry one, so there is no point asking this id again.
            _examinedPanelJobs[printerId] = jobId;

            return null;
        }

        var claimed = candidates.FirstOrDefault(
            candidate => (job.Path is { } path && path == candidate.PrinterPath) ||
                                        (job.DisplayName is { } displayName && displayName == candidate.Entry.PrintFile!.Name));

        _examinedPanelJobs[printerId] = jobId;

        if (claimed is null)
        {
            _logger.LogInformation(
                "[{PrinterId}] firmware job {JobId} is {TheirPath}, which nothing here queued; leaving it alone.",
                printerId, jobId, job.Path ?? job.DisplayName);

            return null;
        }

        _logger.LogWarning(
            "[{PrinterId}] {FileName} was started at the printer, not by a command of ours - adopting "
            + "firmware job {JobId} and consuming the entry so it does not print twice.",
            printerId, claimed.Entry.PrintFile!.Name, jobId);

        PrintJob adopted = new()
        {
            PrinterId = printerId,
            TrackingId = claimed.Entry.TrackingId,
            FileName = claimed.Entry.PrintFile!.Name,
            Digest = claimed.Entry.PrintFile.Digest,
            QueuedByUserId = claimed.Entry.QueuedByUserId,
            QueuedByScope = claimed.Entry.QueuedByScope,
            PrinterPath = claimed.PrinterPath,
            StartedAt = _timeProvider.GetUtcNow(),

            // CommandedAt stays null: that is the record that no command of ours started this.
            State = PrintState.Printing,
            FirmwareJobId = jobId,
        };

        dbContext.PrintJobs.Add(adopted);
        dbContext.QueuedPrints.Remove(claimed.Entry);
        await dbContext.SaveChangesAsync(cancellationToken);

        return adopted;
    }

    /// <summary>
    /// Settles a print that was commanded and never acknowledged, by asking the printer what it is
    /// running.
    /// </summary>
    /// <returns>What was established - see <see cref="PrintStartVerdict"/>.</returns>
    /// <remarks>
    /// <para>
    /// <b>Asking is the whole design, and telemetry is why.</b> A live state carries a
    /// <c>job_id</c> and a status, so it can say a printer is printing <i>something</i>; nothing in
    /// it names a file. Adopting on that alone would attach somebody's queue entry to a print
    /// started at the panel and then delete the entry - the same defect pointing the other way. So
    /// the job id is what telemetry is for, and <c>SEND_JOB_INFO</c> answers who the print belongs
    /// to (Henrik, 2026-08-22: *"the printer can definitively answer the state"*).
    /// </para>
    /// <para>
    /// <b>The evidence expires, which is why this runs at the top of a pass and not when the queue
    /// next tries to advance.</b> A printer can only describe the job it is running now: once the
    /// print ends and somebody clears the bed, a duplicate is indistinguishable from a legitimate
    /// print, and the queue would start one.
    /// </para>
    /// <para>
    /// <b>The ask goes out as whoever queued the work</b>, like every other command this loop sends.
    /// When the entry has gone - cancelled in the window between the row being opened and the printer
    /// answering - there is no authority to borrow and nothing to remove on success, so nothing is
    /// asked and the elapsed-time rules settle it instead. Under-asking there costs a hold nobody is
    /// waiting on; asking with an authority nobody granted would cost more.
    /// </para>
    /// </remarks>
    private async Task<PrintStartVerdict> ResolveUnconfirmedPrintAsync(AsyncServiceScope scope,
                                                                       HomespoolDbContext dbContext,
                                                                       int printerId,
                                                                       PrintJob commanded,
                                                                       PrinterLiveState? live,
                                                                       CancellationToken cancellationToken)
    {
        QueuedPrint? entry = await dbContext.QueuedPrints
                                            .SingleOrDefaultAsync(queued => queued.PrinterId == printerId
                                                                            && queued.TrackingId == commanded.TrackingId,
                                                                  cancellationToken);

        bool connected = _registry.IsConnected(printerId);
        JobAnswer answer = JobAnswer.NotAsked;

        if (connected && entry is not null && live?.JobId is { } jobId)
        {
            answer = await AskWhoseJobAsync(scope, printerId, commanded, entry, jobId, cancellationToken);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();

        PrintStartObservation observation = new(connected,
                                                live?.Status ?? PrinterStatus.Unknown,
                                                live?.LastSeenAt > commanded.StartedAt,
                                                now - commanded.StartedAt,
                                                answer);

        PrintStartVerdict verdict =
            PrintStartRules.Decide(observation, StartUnconfirmedGrace, StartUnresolvableAfter);

        switch (verdict)
        {
            case PrintStartVerdict.Started:
                _logger.LogInformation(
                    "[{PrinterId}] {FileName} was printing after all - the printer took it and answered too late; "
                    + "adopting firmware job {JobId}.",
                    printerId, commanded.FileName, live?.JobId);

                commanded.State = PrintState.Starting;
                commanded.FirmwareJobId = live?.JobId;

                if (entry is not null)
                {
                    // Now, and only now, has the entry done its job. Removing it at command time is
                    // exactly what left a duplicate waiting to run.
                    dbContext.QueuedPrints.Remove(entry);
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                break;

            case PrintStartVerdict.NeverStarted:
                _logger.LogInformation(
                    "[{PrinterId}] {FileName} never started - the printer did not take it. It is still queued.",
                    printerId, commanded.FileName);

                // Removed rather than closed as failed: nothing failed. A command went unanswered
                // for a minute and the printer turned out never to have acted on it, which is not a
                // print and does not belong in a history of prints.
                dbContext.PrintJobs.Remove(commanded);
                await dbContext.SaveChangesAsync(cancellationToken);
                break;

            case PrintStartVerdict.Unresolvable:
                await HoldUnresolvedStartAsync(scope, dbContext, printerId, commanded, entry, now, cancellationToken);
                break;

            default:
                _logger.LogDebug("[{PrinterId}] still waiting to learn whether {FileName} started",
                                 printerId, commanded.FileName);
                break;
        }

        return verdict;
    }

    /// <summary>
    /// Asks the printer which file the job it is reporting belongs to, and compares it with the one
    /// we sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Matched on either name, because the printer volunteers both and each can be absent.</b>
    /// <c>path</c> is the 8.3 alias, which is exactly what <c>START_PRINT</c> was given - it came
    /// from a <c>FILE_INFO</c> in the first place - and <c>display_name</c> is the long name we
    /// uploaded under. Requiring both would refuse a match on a firmware that renders one.
    /// </para>
    /// <para>
    /// <b>A refusal is classified on the prose</b>, as <see cref="HandleRefusal"/> is and for the
    /// same reason: these carry no machine-readable code. The wording is firmware's own, from its
    /// render fixtures, and an unrecognised one falls to
    /// <see cref="JobAnswer.Inconclusive"/> - never to a verdict, because a reason nobody has read
    /// yet must not be allowed to decide anything.
    /// </para>
    /// </remarks>
    private async Task<JobAnswer> AskWhoseJobAsync(AsyncServiceScope scope,
                                                   int printerId,
                                                   PrintJob commanded,
                                                   QueuedPrint entry,
                                                   int jobId,
                                                   CancellationToken cancellationToken)
    {
        PrinterCommandService commands = scope.ServiceProvider.GetRequiredService<PrinterCommandService>();
        CommandOutcome<JobInfoEventDataDTO>? answer;

        try
        {
            answer = await commands.AskAsync(printerId,
                                             new PrusaConnect.Commands.SendJobInfo { JobId = jobId },
                                             CallerFor(entry),
                                             cancellationToken);
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException
                                      or CommandResponseTimedOutException or CommandSendTimedOutException or
                                      TeamAccessDeniedException or CredentialScopeDeniedException
                                      or CommandAnswerUnreadableException)
        {
            _logger.LogDebug(e, "[{PrinterId}] could not ask about firmware job {JobId}", printerId, jobId);

            return JobAnswer.Inconclusive;
        }

        if (answer?.EventType is PrinterEventType.Rejected or PrinterEventType.Failed)
        {
            // "No job in progress" is a negative, but not an instant one: firmware renders it
            // against the machine's momentary state, and a print it has accepted passes through a
            // state with no job before it reports PRINTING - so inside the start window this is
            // what a print that is starting sounds like. The rules weigh it against the grace
            // period rather than trusting it outright.
            return answer.Reason == "No job in progress" ? JobAnswer.NoJob : JobAnswer.Inconclusive;
        }

        if (answer?.Answer is not { } job || (job.Path is null && job.DisplayName is null))
        {
            // A job the printer only remembers renders its state and nothing else - FIN_OK, or
            // FIN_STOPPED. There is no name in it to compare, so it settles nothing.
            return JobAnswer.Inconclusive;
        }

        bool ours = (job.Path is { } path && path == commanded.PrinterPath)
                    || (job.DisplayName is { } displayName && displayName == commanded.FileName);

        if (!ours)
        {
            _logger.LogInformation(
                "[{PrinterId}] firmware job {JobId} is {TheirPath}, not the {OurPath} we asked for; "
                + "the print running here is not ours.",
                printerId, jobId, job.Path ?? job.DisplayName, commanded.PrinterPath);
        }

        return ours ? JobAnswer.Ours : JobAnswer.SomebodyElses;
    }

    /// <summary>
    /// Asks the printer how a print it is no longer running turned out, for a row about to be closed
    /// on a guess.
    /// </summary>
    /// <returns>
    /// <see cref="PrintState.Finished"/> or <see cref="PrintState.Stopped"/> when the printer still
    /// remembers, null when it does not or cannot be asked.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Firmware keeps the outcome of its last two jobs, and answers for them by id.</b> A
    /// <c>SEND_JOB_INFO</c> naming a job that is not the current one is served from that history and
    /// comes back <c>FIN_OK</c> or <c>FIN_STOPPED</c> - and the aborting of a print that never began
    /// is recorded there too, which is exactly the row this loop would otherwise close as
    /// <see cref="PrintState.Unknown"/>.
    /// </para>
    /// <para>
    /// <b>Only ever called where the alternative is <c>Unknown</c>.</b> It cannot make a close
    /// happen, only make one truthful: every caller closes with or without an answer, so a printer
    /// that has gone quiet costs nothing but the attempt. Two jobs is a short memory, and a third
    /// print since is indistinguishable here from a printer that never knew.
    /// </para>
    /// <para>
    /// <b>Asked under the row's own recorded authority</b>, which is why
    /// <see cref="PrintJob.QueuedByScope"/> is carried across from the queue entry: acting as the
    /// user without it would run this with more authority than anybody granted.
    /// </para>
    /// <para>
    /// <b>The null check is an economy, not the safety.</b> A row from before that column has no
    /// credential to borrow, so asking could only fail - <see cref="PrinterCommandService"/> gates
    /// every send on <see cref="Authorisation.PrinterAccessService"/> and refuses a scope that
    /// grants nothing, whatever this method does. Skipping the attempt saves a round trip and an
    /// exception; it is not what prevents the escalation, and should not be read as if it were.
    /// </para>
    /// </remarks>
    private async Task<PrintState?> AskPriorOutcomeAsync(AsyncServiceScope scope,
                                                         int printerId,
                                                         PrintJob job,
                                                         CancellationToken cancellationToken)
    {
        if (job.FirmwareJobId is not { } jobId || job.QueuedByScope is not { } recordedScope)
        {
            return null;
        }

        if (!_registry.IsConnected(printerId))
        {
            return null;
        }

        PrinterCommandService commands = scope.ServiceProvider.GetRequiredService<PrinterCommandService>();
        CommandOutcome<JobInfoEventDataDTO>? answer;

        try
        {
            answer = await commands.AskAsync(printerId,
                                             new PrusaConnect.Commands.SendJobInfo { JobId = jobId },
                                             Caller.Scoped(job.QueuedByUserId, CapabilitySet.Parse(recordedScope)),
                                             cancellationToken);
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException
                                      or CommandResponseTimedOutException or CommandSendTimedOutException or
                                      TeamAccessDeniedException or CredentialScopeDeniedException
                                      or CommandAnswerUnreadableException)
        {
            _logger.LogDebug(e, "[{PrinterId}] could not ask how firmware job {JobId} ended", printerId, jobId);

            return null;
        }

        // "Job ID doesn't match" or "No job in progress" - past the two it keeps, or never known.
        if (answer?.EventType is PrinterEventType.Rejected or PrinterEventType.Failed)
        {
            return null;
        }

        return answer?.Answer?.State switch
        {
            "FIN_OK" => PrintState.Finished,
            "FIN_STOPPED" => PrintState.Stopped,

            // Anything else is the printer describing a job it is still running, which is not what
            // this asks about, or a word nobody here has read. Neither settles an outcome.
            _ => null,
        };
    }

    /// <summary>
    /// Gives up asking, and stops the queue rather than guessing.
    /// </summary>
    /// <remarks>
    /// The row is closed <see cref="PrintState.Unknown"/>, which is what that state is for - it
    /// stopped being observable without saying how - and the hold is what keeps the entry from being
    /// printed a second time on the strength of not knowing. Both, because either alone is wrong:
    /// closing without holding advances the queue onto a print that may already have run, and
    /// holding without closing leaves the printer's one open-print slot occupied for ever.
    /// </remarks>
    private async Task HoldUnresolvedStartAsync(AsyncServiceScope scope,
                                                HomespoolDbContext dbContext,
                                                int printerId,
                                                PrintJob commanded,
                                                QueuedPrint? entry,
                                                DateTimeOffset now,
                                                CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "[{PrinterId}] gave up asking whether {FileName} started: the printer reports a job it will not "
            + "describe. Holding the queue - printing it again might print it twice.",
            printerId, commanded.FileName);

        // Same reasoning as the backstop: this row closes regardless, so ask before recording a
        // guess. The hold below is unaffected either way - knowing how a print ended does not say
        // whether the entry beside it is safe to run again.
        PrintState settled = await AskPriorOutcomeAsync(scope, printerId, commanded, cancellationToken)
                             ?? PrintState.Unknown;

        commanded.Reason = settled == PrintState.Unknown
            ? "The printer never said whether it started this print."
            : "The printer would not describe this print while it ran, and reported afterwards how it ended.";

        Close(commanded, settled, now);

        if (entry is not null)
        {
            PrintFileOnPrinter? onPrinter = await dbContext.PrintFilesOnPrinters
                                                           .SingleOrDefaultAsync(
                                                               row => row.PrinterId == printerId
                                                                      && row.PrintFileId == entry.PrintFileId,
                                                               cancellationToken);

            if (onPrinter is not null)
            {
                onPrinter.HoldReason = PrintHoldReason.PrintStartUnresolved;
                onPrinter.HoldPrinterFreeBytes = null;
                onPrinter.HoldPrinterFileBytes = null;
                onPrinter.BlockedAt = now;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Offers the head's file to the printer, and records that it did.</summary>
    private async Task TransferAsync(AsyncServiceScope scope,
                                     HomespoolDbContext dbContext,
                                     int printerId,
                                     QueuedPrint head,
                                     PrintFileOnPrinter? onPrinter,
                                     CancellationToken cancellationToken)
    {
        PrintFileCatalog catalog = scope.ServiceProvider.GetRequiredService<PrintFileCatalog>();
        StoredFile? file = catalog.FindForPrinting(head.QueuedByUserId, head.PrintFile!.Name);

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
            CommandOutcome? outcome = (await sender.SendAsync(printer, file, CallerFor(head), cancellationToken)).Outcome;

            if (outcome?.EventType is PrinterEventType.Rejected or PrinterEventType.Failed)
            {
                // Classified on MachineReason, not on the prose: the code is a fixed vocabulary and
                // the wording is free to change between releases.
                _logger.LogInformation(
                    "[{PrinterId}] refused the transfer of {FileName}: {Reason} [{MachineReason}]",
                    printerId, file.FileName, outcome.Reason, outcome.MachineReason);

                // Cleared whatever the reason, so the next tick decides afresh rather than waiting
                // out the staleness timeout on a transfer that never started. For everything except
                // FILE_EXISTS that is the whole response - usually the single system-wide transfer
                // slot being busy, where trying again is exactly right.
                onPrinter.TransferStartedAt = null;

                if (outcome.MachineReason == FileExistsCode)
                {
                    await ReconcileExistingFileAsync(scope, printerId, head, file, onPrinter, cancellationToken);
                }

                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException
                                      or CommandResponseTimedOutException or CommandSendTimedOutException or
                                      PrintFileUnreadableException)
        {
            _logger.LogInformation(e, "[{PrinterId}] could not start the transfer of {FileName}",
                                   printerId, file.FileName);
            onPrinter.TransferStartedAt = null;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception e) when (e is TeamAccessDeniedException or CredentialScopeDeniedException)
        {
            // Whoever queued this may no longer use the printer. Leaving the entry in place is
            // deliberate - it is not this loop's business to cancel somebody's print because their
            // permissions changed, and a restored permission resumes it.
            //
            // CredentialScopeDeniedException is here for completeness rather than because it fires:
            // enqueueing requires Print, so a row written by EnqueueAsync always carries what the
            // loop needs. It is reachable by editing the column by hand, and a background service
            // that dies on a hand-edited row is worse than one that logs and moves on.
            _logger.LogWarning("[{PrinterId}] {FileName} is queued by a user who may no longer use this printer",
                               printerId, file.FileName);
            onPrinter.TransferStartedAt = null;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Answers a <c>FILE_EXISTS</c> refusal by asking what is actually on the drive: adopts the file
    /// when it is ours, and holds the queue with a sentence when it is somebody else's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The refusal is a cache miss in our own bookkeeping, not an error.</b> The bytes we wanted to
    /// send are already where we wanted them - put there by an earlier run, by PrusaLink, or by a
    /// person with a USB stick - so the right answer is to record what
    /// <see cref="PrintFileOnPrinter"/> should have said and print. That is the same conclusion
    /// <c>File not found</c> reaches from the other direction: the drive is the truth, and a refusal is
    /// it correcting us.
    /// </para>
    /// <para>
    /// <b>A matching name is not matching content, so the size is checked.</b> It is the only
    /// comparator firmware offers - <c>FILE_INFO</c> carries no digest - which leaves a real residue:
    /// two different files of identical length would be adopted wrongly, and the print would be of
    /// somebody else's model. Judged worth it because the alternative refuses every legitimate
    /// re-queue, and because equal-length-but-different is a coincidence rather than a mechanism.
    /// <b>Sharpening it needs a digest firmware does not send</b>, so do not reach for one here.
    /// </para>
    /// <para>
    /// <b>The path recorded is the one <c>FILE_INFO</c> answers with</b>, not the one we asked about:
    /// that is the 8.3 alias, which is what <c>START_PRINT</c> then uses, and it is unguessable from
    /// here because the counter depends on what else is on that drive.
    /// </para>
    /// </remarks>
    private async Task ReconcileExistingFileAsync(AsyncServiceScope scope,
                                                  int printerId,
                                                  QueuedPrint head,
                                                  StoredFile file,
                                                  PrintFileOnPrinter onPrinter,
                                                  CancellationToken cancellationToken)
    {
        PrinterCommandService commands = scope.ServiceProvider.GetRequiredService<PrinterCommandService>();
        FileInfoEventDataDTO? existing;

        try
        {
            CommandOutcome<FileInfoEventDataDTO>? answer = await commands.AskAsync(
                printerId, new PrusaConnect.Commands.SendFileInfo { Path = file.PrinterPath }, CallerFor(head), cancellationToken);

            existing = answer?.Answer;
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException
                                      or CommandResponseTimedOutException or CommandSendTimedOutException or
                                      TeamAccessDeniedException or CredentialScopeDeniedException
                                      or CommandAnswerUnreadableException)
        {
            // Could not ask. Not a block: the next pass asks again, and holding a queue on an
            // unanswered question would punish a printer that was merely busy.
            _logger.LogDebug(e, "[{PrinterId}] could not ask about the existing {FileName}",
                             printerId, file.FileName);

            return;
        }

        if (existing?.Size == file.Length)
        {
            _logger.LogInformation(
                "[{PrinterId}] {FileName} is already on the drive as {PrinterPath} at the same size; adopting it",
                printerId, file.FileName, existing.Path);

            onPrinter.ArrivedAt = _timeProvider.GetUtcNow();
            onPrinter.PrinterPath = existing.Path ?? file.PrinterPath;
            ClearHold(onPrinter);

            return;
        }

        // Somebody else's file under our name. Held rather than failed, because the entry is still
        // wanted and a person deleting it at the panel should see the queue resume by itself.
        //
        // Two reasons rather than one with a nullable size: "demonstrably not our file" and "cannot
        // be confirmed either way" are different things to tell somebody, and collapsing them would
        // have the page claim a certainty the printer declined to give.
        onPrinter.HoldReason = existing?.Size is not null ?
            PrintHoldReason.FileExistsDifferentSize :
            PrintHoldReason.FileExistsUnknownSize;
        onPrinter.HoldPrinterFreeBytes = null;
        onPrinter.HoldPrinterFileBytes = existing?.Size;
        onPrinter.BlockedAt = _timeProvider.GetUtcNow();

        // English in the log on purpose, and the numbers as fields: this line is read by whoever runs
        // the deployment, while the page says the same thing to whoever is waiting for the print, in
        // their own language.
        _logger.LogWarning(
            "[{PrinterId}] {FileName} is already on the printer as {PrinterBytes} bytes against {OurBytes} here; "
            + "holding the queue.",
            printerId, file.FileName, existing?.Size, file.Length);
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
    private async Task<bool> HasRoomForAsync(AsyncServiceScope scope,
                                             HomespoolDbContext dbContext,
                                             int printerId,
                                             QueuedPrint head,
                                             long length,
                                             PrintFileOnPrinter onPrinter,
                                             CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (onPrinter.HoldReason is not null
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
                await commands.AskAsync(printerId, new PrusaConnect.Commands.SendInfo(), CallerFor(head), cancellationToken);

            free = answer?.Answer?.Storages?
                .FirstOrDefault(storage => storage.MountPoint == "/usb")?
                .FreeSpace;
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException
                                      or CommandResponseTimedOutException or CommandSendTimedOutException or
                                      TeamAccessDeniedException or CredentialScopeDeniedException
                                      or CommandAnswerUnreadableException)
        {
            // Could not ask. Not a block - the next pass asks again, and treating an unanswered
            // question as "no room" would hold a queue on a printer that was merely busy.
            _logger.LogDebug(e, "[{PrinterId}] could not ask about free space", printerId);

            return false;
        }

        if (free is null || free >= length)
        {
            // Only a block this check wrote is a block this check may lift. Since FILE_EXISTS started
            // holding the queue too, "there is room now" is no longer evidence that whatever is in the
            // way has gone - clearing indiscriminately would drop a file-conflict hold every minute
            // and set the transfer retrying against a refusal that has not changed.
            if (onPrinter.HoldReason == PrintHoldReason.InsufficientSpace)
            {
                _logger.LogInformation("[{PrinterId}] there is room for {FileName} now; the queue resumes",
                                       printerId, head.PrintFile!.Name);

                ClearHold(onPrinter);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return true;
        }

        bool newlyBlocked = onPrinter.HoldReason is null;

        onPrinter.HoldReason = PrintHoldReason.InsufficientSpace;
        onPrinter.HoldPrinterFreeBytes = free;
        onPrinter.HoldPrinterFileBytes = null;
        onPrinter.BlockedAt = now;

        if (newlyBlocked)
        {
            // English, and staying that way. PrintJob.Reason is a history record whose other writer is
            // HandleRefusal, passing firmware's own refusal string through verbatim - so the column
            // holds what was said at the time rather than something to re-say later. The live hold is
            // what a reader acts on, and that is HoldReason, which is localised: the two are
            // different jobs.
            string recorded = string.Create(
                CultureInfo.InvariantCulture,
                $"Not enough space on the printer: {head.PrintFile!.Name} needs {length} bytes, {free} free.");

            // Written once, on the transition. A row per tick would turn history into a log, and the
            // queue entry itself stays put - somebody still wants this printed.
            dbContext.PrintJobs.Add(new PrintJob
            {
                PrinterId = printerId,
                TrackingId = head.TrackingId,
                FileName = head.PrintFile.Name,
                Digest = head.PrintFile.Digest,
                QueuedByUserId = head.QueuedByUserId,
                QueuedByScope = head.QueuedByScope,
                StartedAt = now,
                EndedAt = now,
                State = PrintState.Failed,
                Reason = recorded,
            });

            _logger.LogWarning("[{PrinterId}] {Reason} The queue holds until space is freed.", printerId, recorded);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return false;
    }

    /// <summary>Starts the print, and removes the entry once the printer has taken it.</summary>
    /// <remarks>
    /// <para>
    /// <b>Success is not <c>FINISHED</c>.</b> A <c>START_PRINT</c> that took answers <c>JOB_INFO</c>
    /// (planner.cpp:728), so this tests for the absence of a refusal rather than for a particular
    /// event - the check that would otherwise read a started print as an unrecognised answer.
    /// </para>
    /// <para>
    /// <b>And the absence of any answer is not a refusal either.</b> The row is opened
    /// <see cref="PrintState.Unconfirmed"/> <i>before</i> the command goes out, so that a print the
    /// printer accepts but does not acknowledge in time leaves a record of the question rather than
    /// nothing at all. That is not defensive: a timeout is not a negative answer, and it happened because
    /// the printer accepted the command and went off to home and heat - so the timeout is caused by
    /// the success it was being read as ruling out. Writing the row afterwards leaves a window in
    /// which the effect exists and the record does not, which is the same shape
    /// <see cref="PrintFileOnPrinter.TransferStartedAt"/> is written early to close.
    /// </para>
    /// <para>
    /// <b>The queue entry stays until the printer confirms.</b> Removing it on a command that may not
    /// have landed would trade this defect for its mirror image - a queued print silently dropped
    /// because a printer was slow to answer.
    /// </para>
    /// </remarks>
    private async Task PrintAsync(AsyncServiceScope scope,
                                  HomespoolDbContext dbContext,
                                  int printerId,
                                  QueuedPrint head,
                                  string printerPath,
                                  CancellationToken cancellationToken)
    {
        PrinterCommandService commands = scope.ServiceProvider.GetRequiredService<PrinterCommandService>();

        DateTimeOffset now = _timeProvider.GetUtcNow();

        PrintJob commanded = new()
        {
            PrinterId = printerId,
            TrackingId = head.TrackingId,
            FileName = head.PrintFile!.Name,
            Digest = head.PrintFile.Digest,
            QueuedByUserId = head.QueuedByUserId,
            QueuedByScope = head.QueuedByScope,
            PrinterPath = printerPath,
            StartedAt = now,
            CommandedAt = now,
            State = PrintState.Unconfirmed,
        };

        dbContext.PrintJobs.Add(commanded);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            CommandOutcome? outcome = await commands.SendCommandAsync(printerId,
                                                                      new StartPrint(printerPath),
                                                                      CallerFor(head),
                                                                      cancellationToken);

            if (outcome?.EventType is PrinterEventType.Rejected or PrinterEventType.Failed)
            {
                HandleRefusal(printerId, dbContext, head, commanded, outcome.Reason);
                await dbContext.SaveChangesAsync(cancellationToken);

                return;
            }

            _logger.LogInformation("[{PrinterId}] started printing {Path}", printerId, printerPath);

            // Starting rather than Printing: the printer has accepted the command and will keep
            // reporting READY for a few seconds yet. ReconcilePrintAsync promotes it when telemetry
            // says otherwise, and that is also where the firmware job id is picked up.
            commanded.State = PrintState.Starting;

            // The entry has done its job; the history row carries it from here.
            dbContext.QueuedPrints.Remove(head);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception e) when (e is CommandAlreadyInFlightException or TeamAccessDeniedException
                                      or CredentialScopeDeniedException)
        {
            // The three refusals that happen before anything is written to a socket: the in-flight
            // slot is taken, the team says no, the credential says no. Each is a statement that this
            // command did not reach the printer, so the row is removed rather than left as a question
            // nobody needs to answer.
            _logger.LogInformation(e, "[{PrinterId}] did not send a print of {Path}", printerId, printerPath);
            dbContext.PrintJobs.Remove(commanded);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception e) when (e is CommandResponseTimedOutException or CommandSendTimedOutException
                                      or PrinterNotConnectedException)
        {
            // Unknown, and the row stays Unconfirmed to say so. None of these three can claim the
            // command was not acted on: a response timeout is the printer being slow, a send timeout
            // is a write that may still be on the wire, and NotConnected covers a command that was
            // written and left pending when the connection died as well as one never sent at all
            // (PrinterConnectionActor's read-loop finally, against its pre-send checks).
            //
            // ReconcilePrintAsync resolves it by asking the printer, which is the only thing that
            // can. Logged at Warning rather than Information: this is a print in an unknown state,
            // not routine slowness.
            _logger.LogWarning(e, "[{PrinterId}] no answer to starting {Path}; asking the printer what it is doing",
                               printerId, printerPath);
        }
    }

    /// <summary>
    /// Applies the retry rules to a refused <c>START_PRINT</c> - firmware's own reason string decides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only four reasons are reachable and <b>only one is transient</b>: <c>Can't print now</c> is a wrong state or
    /// a <c>print_begin</c> that did not take, and waiting is the whole response.
    /// <c>Forbidden path</c> and <c>Tools mapping not enabled</c> are terminal, and retrying either
    /// would hide a misconfiguration behind a queue that looks merely slow.
    /// </para>
    /// <para>
    /// <c>File not found</c> is the interesting one: it is the drive correcting us. The bytes were
    /// believed present and are not - deleted at the panel, or a card swapped - so the belief is
    /// cleared and the file is offered again rather than the entry being failed.
    /// </para>
    /// <para>
    /// <b>Every arm here settles <paramref name="commanded"/> except one, because a refusal is an
    /// answer - except one.</b> The row was opened before the command went out to survive the case
    /// where no answer comes at all; once the printer has said <i>no</i>, nothing is outstanding. A
    /// terminal refusal closes it as the failed print it is, and a transient one removes it - a row
    /// per retry would turn history into a log of a printer repeating itself. The exception is
    /// <c>No job in progress</c>, which is not the printer saying no: see that arm.
    /// </para>
    /// </remarks>
    private void HandleRefusal(int printerId,
                               HomespoolDbContext dbContext,
                               QueuedPrint head,
                               PrintJob commanded,
                               string? reason)
    {
        switch (reason)
        {
            case "File not found":
                _logger.LogInformation("[{PrinterId}] the drive no longer has {FileName}; sending it again",
                                       printerId, head.PrintFile?.Name);

                dbContext.PrintFilesOnPrinters
                         .Where(row => row.PrinterId == printerId && row.PrintFileId == head.PrintFileId)
                         .ExecuteDelete();

                dbContext.PrintJobs.Remove(commanded);
                break;

            case "No job in progress":
                // The ack lying, not the printer refusing. Firmware renders the JOB_INFO answer to
                // a START_PRINT against its momentary state, and a print it has accepted passes
                // through a state that reports READY with no job before it reports PRINTING - so
                // this rejection arrives, command id and all, for a print that is starting. Nothing
                // is settled: the row stays Unconfirmed, the entry stays queued, and
                // ResolveUnconfirmedPrintAsync settles it by asking, exactly as for a timeout.
                // Removing the row here is how a phantom print is minted - the effect exists, the
                // record does not, and the entry survives to print the file a second time.
                _logger.LogWarning(
                    "[{PrinterId}] START_PRINT for {Path} was answered \"No job in progress\", which firmware " +
                    "also says about a print that is starting; treating it as unanswered and asking the printer.",
                    printerId, commanded.PrinterPath);
                break;

            case "Forbidden path":
            case "Tools mapping not enabled":
                _logger.LogError(
                    "[{PrinterId}] refused {FileName} with \"{Reason}\", which will not change by retrying; " +
                    "removing it from the queue.",
                    printerId, head.PrintFile?.Name, reason);

                // Recorded as a failed print rather than only logged. Dropping the entry with nothing
                // to show for it is how a queued print used to vanish with no way for its owner to
                // find out why. It spans the ask and nothing else, which is honest: nothing printed.
                commanded.Reason = reason;
                Close(commanded, PrintState.Failed, _timeProvider.GetUtcNow());

                dbContext.QueuedPrints.Remove(head);
                break;

            default:
                // "Can't print now", and anything a future firmware adds. Waiting is free and the next
                // tick asks again; treating an unrecognised reason as terminal would throw away a
                // print for a string nobody has read yet.
                _logger.LogDebug("[{PrinterId}] not printing yet: {Reason}", printerId, reason);
                dbContext.PrintJobs.Remove(commanded);
                break;
        }
    }
}
