using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Host.Exceptions;
using Homespool.Host.Printing;
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
/// <b>A hosted service rather than anything inside <c>PrinterConnectionActor</c>.</b>
/// The actor has no database access by design - that is what keeps its
/// loop single-threaded and free of permission checks - so the thing that reads queues and writes
/// rows lives out here and talks to the actor the same way a person does, through
/// <see cref="PrinterCommandService"/>.
/// </para>
/// <para>
/// <b>It acts as the user who queued the print.</b> The loop is not a principal and must not become a
/// way around <see cref="Capability.Print"/>: every command goes out under
/// <see cref="QueuedPrint.QueuedByUserId"/>, so a member whose access is revoked between queueing and
/// printing - a closed account included - stops advancing, and the rules say why
/// (<see cref="QueueWaitReason.QueuerLostAccess"/>). That is also the only handle on <i>whose</i>
/// file it is, since the store is keyed by user.
/// </para>
/// <para>
/// <b>Getting a file onto the drive is <see cref="TransferService"/>'s</b>, which a direct send shares,
/// and which reads the printer's reports of how each transfer ended. The queue decides around it -
/// whether to send, and what a refusal or an ending means for the queue - through
/// <see cref="QueueTransferPolicy"/> and <see cref="QueueTransferEndings"/>, and settles a printer's
/// transfers before each pass, so a transfer that finished since the last one is known before
/// anything is decided on the assumption that it has not.
/// </para>
/// <para>
/// <b>Everything it needs is persisted, so a tick is stateless.</b> It holds no per-printer memory
/// between passes beyond the last panel job examined, an optimisation rather than state: losing it
/// costs a repeated question, not correctness. A
/// restart therefore resumes without ceremony, and the design's "nudged on enqueue and on connect"
/// is a latency improvement over the timer rather than the mechanism. The one exception is whether
/// this run has watched the open print's filament before it moved, and a restart forgetting it
/// costs only a <see cref="PrintJob.BegunAt"/> that could not have been told honestly anyway.
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
    /// 100 MB is close to six. This exists for the case with no other bound: a transfer the printer
    /// has reported whose terminal event never arrives - a printer that goes away mid-transfer and
    /// does not come back to fetch again - leaves <see cref="FileOnPrinter.TransferStartedAt"/>
    /// set with nothing running, which without this would wedge that printer's queue permanently.
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
    /// How long after a file has arrived the loop waits for the printer to name it before asking.
    /// </summary>
    /// <remarks>
    /// The name ordinarily comes first: firmware sends the <c>FILE_INFO</c> a few seconds into a
    /// transfer, long before it reports the transfer finished, so an arrived file without one is
    /// already late. The wait covers the two reports reaching the database in either order.
    /// </remarks>
    public static readonly TimeSpan PathAskAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long after a file has arrived the loop goes on asking the printer what it called it before
    /// it gives up and holds the queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reached only by a printer that is connected and will not name a file it holds</b> - every
    /// ask unanswered, or answered with nothing to print by, for a quarter of an hour. A file that is
    /// not there is told apart at the first answer, and sent again.
    /// </para>
    /// <para>
    /// <b>There is a bound at all because the wait had none</b>, and a queue stopped behind a sentence
    /// saying it is waiting for the printer reads as working while it never moves. It holds rather
    /// than guessing at the 8.3 name, because a wrong guess prints a different file. See
    /// <see cref="PrintHoldReason.PrinterPathUnknown"/>.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan PathUnresolvableAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How much of a path or a reason the printer wrote is worth a log line. A drive path tops out
    /// near 260 characters; an event may be a megabyte, and its strings are the sender's to size.
    /// </summary>
    private const int MaxLoggedLength = 256;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PrinterConnectionRegistry _registry;
    private readonly TransferService _transfers;
    private readonly QueueSignal _signal;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<QueueAdvancer> _logger;

    /// <summary>
    /// When this process began, on the advancer's own clock - the line between what the printer has
    /// said to this run and what an earlier run left behind.
    /// </summary>
    /// <remarks>
    /// Live state can predate the process. With telemetry held in memory the store is seeded at
    /// startup from what the last shutdown saved, and that save can be days old if a more recent one
    /// failed; with it on disk the row is simply whatever was last written. Either way a status is
    /// only the printer's word about now if it arrived after this. See the open-print arm of
    /// <see cref="ReconcilePrintAsync"/>.
    /// </remarks>
    private readonly DateTimeOffset _startedAt;

    /// <summary>
    /// The last firmware job id each printer was asked about by
    /// <see cref="TryAdoptPanelPrintAsync"/> and found not to be ours - so a stranger's print costs
    /// one question, not one per pass.
    /// </summary>
    /// <remarks>
    /// Memory as an optimisation rather than state: losing it - a restart - costs one repeated
    /// <c>SEND_JOB_INFO</c>, not correctness. Concurrent because the passes of different printers run
    /// side by side; each printer's own pass is the only one that touches its entry.
    /// </remarks>
    private readonly ConcurrentDictionary<int, int> _examinedPanelJobs = [];

    /// <summary>
    /// Per printer, the open print this run has seen with its filament odometer still at the opening
    /// reading - the only print whose first rise this run can vouch for.
    /// </summary>
    /// <remarks>
    /// <b>Unlike the two above, state rather than an optimisation, and losing it is the point.</b> A
    /// restart forgets it, so a reading that rose while nothing was listening is never written as
    /// <see cref="PrintJob.BegunAt"/> - that would be the moment of the restart, not of the extrusion.
    /// See <see cref="ObserveFilament"/>.
    /// </remarks>
    private readonly ConcurrentDictionary<int, long> _seenAtOpeningReading = [];

    /// <summary>
    /// Per printer, the <i>(file, printer)</i> row last asked what the printer calls it, and when - so
    /// a file waiting on its name costs a question per <see cref="BlockRecheckAfter"/>, not per pass.
    /// </summary>
    /// <remarks>
    /// An optimisation rather than state, like <see cref="_examinedPanelJobs"/>: a restart costs one
    /// question asked early. The bound on asking is <see cref="FileOnPrinter.ArrivedAt"/>, which is
    /// stored. See <see cref="AskForPrinterPathAsync"/>.
    /// </remarks>
    private readonly ConcurrentDictionary<int, (long row, DateTimeOffset at)> _pathAsked = [];

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

    /// <summary>The pass each printer is in the middle of, as the producer loop started it. Read only by that loop.</summary>
    private readonly Dictionary<int, Task> _running = [];

    public QueueAdvancer(IServiceScopeFactory scopeFactory,
                         PrinterConnectionRegistry registry,
                         TransferService transfers,
                         QueueSignal signal,
                         TimeProvider timeProvider,
                         ILogger<QueueAdvancer> logger)
    {
        _scopeFactory = scopeFactory;
        _registry = registry;
        _transfers = transfers;
        _signal = signal;
        _timeProvider = timeProvider;
        _logger = logger;
        _startedAt = timeProvider.GetUtcNow();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(PollInterval, stoppingToken);
                await StartPassesAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }

        await EndRunningPassesAsync();
    }

    /// <summary>
    /// One pass over every printer that needs one, side by side, waiting for them all. Public so a
    /// test can drive it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The producer loop does not call this</b>: it starts the passes and goes back to waiting for
    /// its next tick (<see cref="StartPassesAsync"/>), so a printer that takes half a minute to answer
    /// holds up its own queue and no other. This is the same passes, awaited.
    /// </para>
    /// <para>
    /// <b>Throws only on cancellation.</b> Anything else escaping here ends <see cref="ExecuteAsync"/>,
    /// and a faulted background service stops the host - so a busy database would restart the whole
    /// app, and every printer would reconnect, where it should only have cost one tick.
    /// </para>
    /// </remarks>
    public async Task AdvanceAllAsync(CancellationToken cancellationToken)
    {
        List<int>? printerIds = await FindPrintersAsync(cancellationToken);

        if (printerIds is null)
        {
            return;
        }

        await Task.WhenAll(printerIds.Select(printerId => StartPass(printerId, cancellationToken)));
    }

    /// <summary>
    /// Starts a pass for each printer that needs one and is not still in the middle of its last, and
    /// leaves them to run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not awaited, so that no printer holds up another.</b> The passes used to run one printer
    /// after another, and a send waiting out a silent printer's response timeout held up every queue
    /// behind it - found in review (2026-10-02) for the transfers, and the same for any command. How
    /// many passes <i>work</i> at once is the <see cref="QueueWorkBudget"/>'s to limit; how many
    /// <i>wait</i> on printers is not limited, because waiting costs nothing.
    /// </para>
    /// <para>
    /// A pass still running at the next tick is left to finish rather than given a second one. This
    /// loop is the only thing that reads <see cref="_running"/>, so it takes no lock.
    /// </para>
    /// </remarks>
    private async Task StartPassesAsync(CancellationToken cancellationToken)
    {
        foreach (int finished in _running.Where(pass => pass.Value.IsCompleted).Select(pass => pass.Key).ToList())
        {
            _running.Remove(finished);
        }

        List<int>? printerIds = await FindPrintersAsync(cancellationToken);

        if (printerIds is null)
        {
            return;
        }

        foreach (int printerId in printerIds)
        {
            if (_running.ContainsKey(printerId))
            {
                _logger.LogDebug("[{PrinterId}] the last pass is still running; leaving it to that one", printerId);

                continue;
            }

            _running[printerId] = StartPass(printerId, cancellationToken);
        }
    }

    /// <summary>Waits for the passes still running at shutdown, which have been told to stop.</summary>
    private async Task EndRunningPassesAsync()
    {
        try
        {
            await Task.WhenAll(_running.Values);
        }
        catch (OperationCanceledException)
        {
            // Told to stop, and did.
        }

        _running.Clear();
    }

    /// <summary>The printers that need a pass, or null - logged - when they could not be found.</summary>
    private async Task<List<int>?> FindPrintersAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            HomespoolDbContext dbContext = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

            return await PrintersNeedingAPassAsync(dbContext, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Finding the printers that need a queue pass failed.");

            return null;
        }
    }

    /// <summary>
    /// One printer's pass, on the thread pool: the database's calls complete in place, so a pass not
    /// moved off the caller would run to its first real wait before the next printer's began.
    /// </summary>
    private Task StartPass(int printerId, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
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
        }, CancellationToken.None);
    }

    /// <summary>
    /// The authority a queue entry was accepted under - <b>not merely the person who queued it</b>.
    /// </summary>
    /// <remarks>
    /// The loop has no credential of its own, so it acts on the one recorded when the work was
    /// accepted. Acting as the user alone would run their work with more authority than the token that
    /// queued it, which is privilege escalation across a time boundary: the membership half is
    /// re-checked at send time, and this is what re-checks the credential half beside it.
    /// </remarks>
    internal static Caller CallerFor(QueuedPrint head)
    {
        return Caller.Scoped(head.QueuedByUserId, CapabilitySet.Parse(head.QueuedByScope));
    }

    /// <summary>
    /// The same authority, as a print row carries it across from its entry - or null for a row with
    /// none to lend, opened before <see cref="PrintJob.QueuedByScope"/> existed.
    /// </summary>
    /// <remarks>
    /// <b>The row's copy is the entry's authority, not a lesser one.</b> It is what lets the loop ask
    /// about a print after its entry has gone - consumed by the start, or withdrawn while the start
    /// was unanswered - and it is no wider than what queued the work.
    /// </remarks>
    internal static Caller? CallerFor(PrintJob job)
    {
        return job.QueuedByScope is string scope ? Caller.Scoped(job.QueuedByUserId, CapabilitySet.Parse(scope)) : null;
    }

    /// <summary>
    /// The authority of whoever withdrew this print while it was starting, or null when nobody did.
    /// </summary>
    /// <remarks>
    /// <b>A missing scope parses to nothing</b>, which every send refuses - so a row that somehow
    /// carries a withdrawer without one is never acted on as them.
    /// </remarks>
    private static Caller? WithdrawerOf(PrintJob job)
    {
        return job.WithdrawnByUserId is long withdrawnBy ?
            Caller.Scoped(withdrawnBy, CapabilitySet.Parse(job.WithdrawnByScope)) :
            null;
    }

    /// <summary>
    /// Every printer that needs a pass - one with work waiting, or one with a print in flight.
    /// </summary>
    /// <remarks>
    /// <b>Two conditions because the pass does two jobs.</b> It advances the queue and it reconciles
    /// the open print. Scheduled on queued work alone, a printer would go unvisited from the moment
    /// its last queue entry is consumed at <c>START_PRINT</c> - exactly when its print row still needs
    /// closing - so the last print of a session would never close, and a row stuck
    /// <see cref="PrintState.Starting"/> would block the next print for
    /// <see cref="StartingStaleAfter"/>. <see cref="ReconcilePrintAsync"/> is the half that needs the
    /// second condition.
    /// <para>
    /// <c>Union</c> dedupes in SQL, and the second arm is served by the same partial unique index
    /// (<c>PrinterId WHERE EndedAt IS NULL</c>) that enforces one active print per printer. Still
    /// self-limiting: the row closes, the queue is empty, the printer drops off the list.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Takes an open print's opening filament reading, and then watches for the reading to rise past
    /// it - which is <see cref="PrintJob.BegunAt"/>.
    /// </summary>
    /// <returns>Whether the row changed and needs saving.</returns>
    /// <remarks>
    /// <para>
    /// <b>Only a reading this run heard during this print counts</b>, on both halves. The odometer is
    /// sent only while the printer has a job, so the value standing at the command belongs to the
    /// previous print; and live state can outlive the process, so a value standing after a restart
    /// belongs to the run before.
    /// </para>
    /// <para>
    /// <b>No opening reading for a row opened before this run started</b>: the first report after a
    /// restart may already be past the first extrusion, and an opening reading taken there would make
    /// the next pass's rise look like the start. Nor for one adopted from the panel, which was noticed
    /// rather than started, for the same reason.
    /// </para>
    /// <para>
    /// <b>A rise counts only when this run saw the reading at or below the opening one first.</b>
    /// Retractions dip it mid-preamble and that is still "not yet", so at-or-below rather than equal.
    /// </para>
    /// </remarks>
    private bool ObserveFilament(int printerId, PrintJob active, PrinterLiveState? live, DateTimeOffset now)
    {
        if (active.State is not (PrintState.Starting or PrintState.Printing) ||
            active.CommandedAt is null ||
            active.BegunAt is not null)
        {
            return false;
        }

        if (live?.FilamentUsed is not float reading ||
            live.FilamentUsedAt is not DateTimeOffset heardAt ||
            heardAt < active.StartedAt ||
            heardAt < _startedAt)
        {
            return false;
        }

        if (active.FilamentAtStart is not float opening)
        {
            if (active.StartedAt < _startedAt)
            {
                return false;
            }

            active.FilamentAtStart = reading;
            _seenAtOpeningReading[printerId] = active.Id;

            return true;
        }

        if (reading <= opening)
        {
            _seenAtOpeningReading[printerId] = active.Id;

            return false;
        }

        if (!_seenAtOpeningReading.TryRemove(printerId, out long watched) || watched != active.Id)
        {
            return false;
        }

        active.BegunAt = now;

        _logger.LogInformation("[{PrinterId}] {FileName} began extruding {Preamble:F0} s after it was started",
                               printerId, active.FileName, (now - active.StartedAt).TotalSeconds);

        return true;
    }

    /// <summary>
    /// The odometer as the print ends, for <see cref="PrintJob.FilamentUsed"/> - when this run heard
    /// it during the print, and the print has an opening reading to subtract it from.
    /// </summary>
    private void RecordFilamentAtEnd(PrintJob job, PrinterLiveState? live)
    {
        if (job.FilamentAtStart is not null &&
            live?.FilamentUsed is float reading &&
            live.FilamentUsedAt is DateTimeOffset heardAt &&
            heardAt >= job.StartedAt &&
            heardAt >= _startedAt)
        {
            job.FilamentAtEnd = reading;
        }
    }

    /// <summary>
    /// A path or a reason the printer wrote, as a log line may carry it.
    /// </summary>
    /// <remarks>
    /// That includes <see cref="FileOnPrinter.PrinterPath"/> read back from a row: it is the
    /// printer's own name for the file, stored as it was reported, so having been in the database
    /// does not make it ours.
    /// </remarks>
    internal static string ForLog(string? printerWritten)
    {
        return LogText.Clean(printerWritten, MaxLoggedLength);
    }

    /// <summary>
    /// Waits for a printer's answer without holding the pass's permit - see
    /// <see cref="QueueWorkBudget"/> - through the scope every step of a pass already carries.
    /// </summary>
    private static Task<T> WhilePrinterAnswersAsync<T>(AsyncServiceScope scope, Func<Task<T>> wait)
    {
        return scope.ServiceProvider.GetRequiredService<QueuePassWork>().WhilePrinterAnswersAsync(wait);
    }

    private async Task AdvanceOnceAsync(int printerId, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

        // Before anything is read: this pass works only while it holds a permit, and hands it back
        // whenever it waits on the printer. The scope gives it up if the pass ends any other way.
        QueuePassWork work = scope.ServiceProvider.GetRequiredService<QueuePassWork>();
        await work.BeginAsync(cancellationToken);

        HomespoolDbContext dbContext = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        // What the printer has told us, which StorageOptions.TelemetryInMemory may keep in a database
        // of its own. Read by printer id, so nothing here has to join across the two.
        TelemetryDbContext telemetry = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();

        // The printer's own reports of its transfers first, so a transfer that finished since the
        // last pass is known before anything is decided on the assumption that it has not - unless a
        // send to the printer is waiting or under way, which this pass must never wait behind: the
        // passes run one printer after another, so it would hold up every queue for the printer's
        // response timeout. What is already settled will do; the reports are settled as they arrive.
        if (!await _transfers.SettleUnlessSendingAsync(printerId, cancellationToken))
        {
            _logger.LogDebug("[{PrinterId}] a send is under way; deciding on the reports already settled", printerId);
        }

        PrinterLiveState? live = await telemetry.PrinterLiveStates
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

        // Tracked, unlike the reader's copy, because this one may be removed.
        QueuedPrint head = await dbContext.QueuedPrints
                                          .Include(queued => queued.File)
                                          .SingleAsync(queued => queued.Id == snapshot.Head.QueuedPrintId,
                                                       cancellationToken);

        QueueAction action = QueueRules.Decide(snapshot);

        switch (action.Kind)
        {
            case QueueActionKind.Transfer:
                await TransferAsync(work, printerId, head, cancellationToken);
                break;

            case QueueActionKind.Print:
                await PrintAsync(scope, dbContext, printerId, head, action.Head!.PrinterPath!, cancellationToken);
                break;

            case QueueActionKind.Wait when action.Reason is QueueWaitReason.InsufficientSpace or QueueWaitReason.FileUnreadable:
                // Routed into the transfer path rather than merely logged, because that path is what
                // *re-checks* the drive and clears the block - it begins by asking, and only then
                // sends. Teaching the rules about the block (so a page could not report a transfer
                // that cannot happen) made this necessary: without it the block is self-perpetuating,
                // the rules refusing to transfer and nothing left able to discover there is room now.
                // Caught by the end-to-end test that frees space and expects the queue to resume.
                // An unreadable file comes the same way, for the same reason: only trying to send it
                // finds out that it can be read now.
                await TransferAsync(work, printerId, head, cancellationToken);
                break;

            case QueueActionKind.Wait when action.Reason is QueueWaitReason.AwaitingPrinterPath:
                // The one wait nothing else ends: the report it waits for has already been missed, so
                // only asking the printer can bring it.
                await AskForPrinterPathAsync(scope, dbContext, printerId, head, cancellationToken);
                break;

            case QueueActionKind.Wait:
                _logger.LogDebug("[{PrinterId}] queue holding: {Reason}", printerId, action.Reason);
                break;

            default:
                break;
        }
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

        // A different job id is a different print, whatever the status says. Firmware assigns one
        // per print, before the preview's questions, and keeps it through pauses, attention and a
        // power-panic resume - so the row's print ended in a gap (a restart, a dropped connection,
        // a start backed out of at the preview) and the printer has since started another. Left
        // open, the row would hand that print to its owner, to stop and to cancel objects in, and
        // close on its outcome; a Starting row would be promoted onto it. Asked before anything
        // else reads the live state, so neither the odometer nor the status below is credited to
        // this row.
        if (active.State is PrintState.Starting or PrintState.Printing &&
            active.FirmwareJobId is int recorded &&
            live is not null &&
            live.LastSeenAt >= _startedAt &&
            live.JobId is int running &&
            running != recorded)
        {
            // Not the filament reading, which belongs to the print now running.
            PrintState settled = await AskPriorOutcomeAsync(scope, printerId, active, cancellationToken) ??
                                 PrintState.Unknown;

            _logger.LogInformation("[{PrinterId}] {FileName} was firmware job {JobId} and the printer is running job {RunningJobId}; " +
                                   "it ended while nobody here was listening: {Outcome}",
                                   printerId, active.FileName, recorded, running, settled);

            Close(active, settled, now);
            await dbContext.SaveChangesAsync(cancellationToken);

            return await TryAdoptPanelPrintAsync(scope, dbContext, printerId, live, cancellationToken);
        }

        // Withdrawn while it was being started, and now known to be ours and under way: the person
        // who withdrew it asked for it not to print. Accepted, the stop is recorded on the row by the
        // stop service rather than on this tracked copy, so the next pass - reading the row afresh -
        // is the one that closes it. Refused or unanswered, this pass goes on as usual, which is
        // also what closes a print that ended before the stop could reach it.
        if (active.State is PrintState.Starting or PrintState.Printing &&
            active.WithdrawnByUserId is not null &&
            active.StoppedByUserId is null &&
            await StopWithdrawnPrintAsync(scope, dbContext, printerId, active, cancellationToken))
        {
            return active;
        }

        if (ObserveFilament(printerId, active, live, now))
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (active.State == PrintState.Starting)
        {
            // The job id is the evidence, and it arrives whether or not Printing is ever sampled.
            // Firmware assigns one the moment it accepts, and keeps reporting it through a preview
            // dialog - so recording it here is what lets a later withdrawal mean something. Ours by
            // construction: a printer already running somebody else's job refuses START_PRINT, so
            // the only job it can be reporting seconds after accepting ours is ours.
            if (live?.JobId is int offered && active.FirmwareJobId is null)
            {
                active.FirmwareJobId = offered;
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            // A stop the printer accepted, of a print that never began, is settled: there is no
            // natural completion it could be confused with, so nothing is left to observe or ask.
            // Decided here rather than by PrintStopService, which only records who stopped it, so
            // this loop is the one writer of how a print ended. Ahead of the promotion, because a
            // Printing sample read before the stop must not carry a stopped print into the
            // running phase - and ahead of the withdrawn job id, because a stop in the first
            // seconds can land before any pass has recorded one.
            if (active.StoppedByUserId is not null)
            {
                _logger.LogInformation("[{PrinterId}] {FileName} was stopped before it began; closing it rather than holding the queue.",
                                       printerId, active.FileName);

                Close(active, PrintState.Stopped, now);
                await dbContext.SaveChangesAsync(cancellationToken);

                return null;
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

                RecordFilamentAtEnd(active, live);
                Close(active, said, now);
                await dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("[{PrinterId}] {FileName} ended before it began: {Outcome}",
                                       printerId, active.FileName, said);

                return null;
            }

            // Taken up, and now withdrawn: we hold a job id, the printer reports none, and it is not
            // in any state that could still be starting. Whatever ended it - an Abort at the panel,
            // a refusal we never saw, or a stop of ours whose attribution has not landed yet - it is
            // over, and this is the only signal a panel abort gives us. It sends no event at all,
            // unlike our own STOP_PRINT, which is why closing on the job id rather than on an event
            // covers both.
            //
            // The FirmwareJobId guard is what keeps a legitimate start alive: firmware reports
            // Idle or Ready through PrintInit while it opens the file, and carries no job id yet -
            // so a row that has never seen one is still starting, not finished.
            //
            // Asked before settling for Unknown: firmware records an abort at the preview in the
            // same two-job memory as a print that ran, and answers FIN_STOPPED for it.
            if (active.FirmwareJobId is not null &&
                live?.JobId is null &&
                status is PrinterStatus.Idle or PrinterStatus.Ready or PrinterStatus.Error)
            {
                PrintState withdrawn = await AskPriorOutcomeAsync(scope, printerId, active, cancellationToken) ??
                                       PrintState.Unknown;

                _logger.LogInformation("[{PrinterId}] {FileName} was accepted as firmware job {JobId} and never began; the printer " +
                                       "is {Status} and reports no job, so it is over: {Outcome}",
                                       printerId, active.FileName, active.FirmwareJobId, status, withdrawn);

                Close(active, withdrawn, now);
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
            PrintState settled = await AskPriorOutcomeAsync(scope, printerId, active, cancellationToken) ??
                                 PrintState.Unknown;

            _logger.LogWarning("[{PrinterId}] {FileName} was accepted {Elapsed:F0} minutes ago and never started " +
                               "printing; closing it as {Outcome} so the queue is not wedged.",
                               printerId, active.FileName, (now - active.StartedAt).TotalMinutes, settled);

            Close(active, settled, now);
            await dbContext.SaveChangesAsync(cancellationToken);

            return null;
        }

        // An open print ends only on something the printer has said to this process. No live state,
        // or live state older than the process, is a printer not yet heard from - after a restart it
        // is every printer, for the seconds to minutes a Buddy printer takes to reconnect - and the
        // queue's first pass runs long before that. Read as "stopped printing", that silence closes a
        // running print's row on every restart where telemetry does not outlive the process.
        //
        // Freshness rather than connectivity, deliberately. A printer that reported Stopped and was
        // then switched off has said how its print ended, and that report is still acted on; what
        // waits is only a status nobody here has heard it give. No bound on the wait, which is what a
        // durable live state saying Printing always amounted to for a printer that never came back.
        if (live is null || live.LastSeenAt < _startedAt)
        {
            _logger.LogDebug("[{PrinterId}] {FileName} is open and the printer has not reported since startup; waiting to hear from it.",
                             printerId, active.FileName);

            return active;
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

            // Idle, Ready, Error, or an Unknown the printer actually reported - never an absence,
            // which the guard above has already turned into a wait. It stopped printing and did not
            // say how, so ask before settling for Unknown: firmware keeps the outcome of its last
            // two jobs, and this is the ordinary shape of a print that ended while nobody here was
            // listening - the printer comes back already Ready, its Finished screen long dismissed.
            _ => await AskPriorOutcomeAsync(scope, printerId, active, cancellationToken) ?? PrintState.Unknown,
        };

        // Only on the printer's own word. An ending that had to be asked about happened while
        // nobody here was listening, and the last reading heard falls short by whatever was
        // printed unheard.
        if (status is PrinterStatus.Finished or PrinterStatus.Stopped)
        {
            RecordFilamentAtEnd(active, live);
        }

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
        if (live?.JobId is not int jobId ||
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
        // nothing to ask. Only a copy of the file as it is now: a panel print of a version since
        // overwritten is not the entry, and claiming it would record the newer file as printed.
        var candidates = await dbContext.QueuedPrints
                                        .Include(queued => queued.File)
                                        .Join(dbContext.FilesOnPrinters,
                                              queued => new { queued.PrinterId, queued.FileId },
                                              onPrinter => new { onPrinter.PrinterId, onPrinter.FileId },
                                              (queued, onPrinter) => new
                                              {
                                                  Entry = queued,
                                                  onPrinter.PrinterPath,
                                                  onPrinter.DriveName,
                                                  Current = onPrinter.Digest != null && onPrinter.Digest == queued.File!.Digest,
                                              })
                                        .Where(candidate => candidate.Entry.PrinterId == printerId &&
                                                            candidate.PrinterPath != null &&
                                                            candidate.Current)
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
            answer = await WhilePrinterAnswersAsync(scope,
                                                    () => commands.AskAsync(printerId,
                                                                            new PrusaConnect.Commands.SendJobInfo { JobId = jobId },
                                                                            CallerFor(candidates[0].Entry),
                                                                            cancellationToken));
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException or
                                      CommandResponseTimedOutException or CommandSendTimedOutException or
                                      TeamAccessDeniedException or CredentialScopeDeniedException or
                                      CommandAnswerUnreadableException)
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

        JobInfoEventDataDTO? job = answer?.Answer;

        if (job is null || (job.Path is null && job.DisplayName is null))
        {
            // A job described without a name settles nothing - and a *current* job should always
            // carry one, so there is no point asking this id again.
            _examinedPanelJobs[printerId] = jobId;

            return null;
        }

        var claimed = candidates.FirstOrDefault(
            candidate => (job.Path is not null && job.Path == candidate.PrinterPath) ||
                                        (job.DisplayName is not null &&
                                         job.DisplayName == (candidate.DriveName ?? candidate.Entry.File!.Name)));

        _examinedPanelJobs[printerId] = jobId;

        if (claimed is null)
        {
            _logger.LogInformation(
                "[{PrinterId}] firmware job {JobId} is {TheirPath}, which nothing here queued; leaving it alone.",
                printerId, jobId, ForLog(job.Path ?? job.DisplayName));

            return null;
        }

        _logger.LogWarning(
            "[{PrinterId}] {FileName} was started at the printer, not by a command of ours - adopting " +
            "firmware job {JobId} and consuming the entry so it does not print twice.",
            printerId, claimed.Entry.File!.Name, jobId);

        PrintJob adopted = new()
        {
            PrinterId = printerId,
            PrintUuid = claimed.Entry.PrintUuid,
            FileName = claimed.Entry.File!.Name,
            Digest = claimed.Entry.File.Digest,
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
        await ForgetStartRefusalsAsync(dbContext, printerId, claimed.Entry.FileId, cancellationToken);

        return adopted;
    }

    /// <summary>
    /// Stops a print whose queue entry was withdrawn while it was being started, as the person who
    /// withdrew it.
    /// </summary>
    /// <returns>Whether the printer accepted the stop.</returns>
    /// <remarks>
    /// <para>
    /// <b>Only once the print is known to be ours and running.</b> Called on a
    /// <see cref="PrintState.Starting"/> or <see cref="PrintState.Printing"/> row, which a withdrawn
    /// start reaches only by the printer confirming it or naming our file - never on a status alone,
    /// which cannot say whose print is running.
    /// </para>
    /// <para>
    /// <b>Through <see cref="PrintStopService"/>, like every stop</b>, so the permission is checked
    /// again as it goes out - the same rule the withdrawal was allowed under - and the stop is
    /// attributed to that person when the printer accepts it. A refusal on permission is final: the
    /// request is dropped and the print runs on, for whoever may stop it, rather than being asked
    /// about on every pass. Anything else - a refusal from the printer, no answer, another command
    /// in flight - is tried again next pass, until the stop is accepted or the print ends.
    /// </para>
    /// </remarks>
    private async Task<bool> StopWithdrawnPrintAsync(AsyncServiceScope scope,
                                                     HomespoolDbContext dbContext,
                                                     int printerId,
                                                     PrintJob withdrawn,
                                                     CancellationToken cancellationToken)
    {
        Caller? withdrawer = WithdrawerOf(withdrawn);

        if (withdrawer is null)
        {
            return false;
        }

        PrintStopService stops = scope.ServiceProvider.GetRequiredService<PrintStopService>();
        CommandOutcome? outcome;

        try
        {
            outcome = await WhilePrinterAnswersAsync(scope,
                                                     () => stops.StopAsync(printerId, withdrawer, cancellationToken));
        }
        catch (Exception e) when (e is TeamAccessDeniedException or CredentialScopeDeniedException)
        {
            _logger.LogWarning(e,
                               "[{PrinterId}] {FileName} was withdrawn while starting, but user {UserId} may no longer stop it; " +
                               "leaving it to print.",
                               printerId, withdrawn.FileName, withdrawer.UserId);

            withdrawn.WithdrawnByUserId = null;
            withdrawn.WithdrawnByScope = null;
            await dbContext.SaveChangesAsync(cancellationToken);

            return false;
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException or
                                      CommandResponseTimedOutException or CommandSendTimedOutException)
        {
            _logger.LogDebug(e, "[{PrinterId}] could not stop the withdrawn {FileName} yet", printerId, withdrawn.FileName);

            return false;
        }

        if (outcome?.EventType is PrinterEventType.Rejected or PrinterEventType.Failed)
        {
            _logger.LogDebug("[{PrinterId}] the printer would not stop the withdrawn {FileName} yet: {Reason}",
                             printerId, withdrawn.FileName, ForLog(outcome.Reason));

            return false;
        }

        _logger.LogInformation("[{PrinterId}] stopped {FileName}, which user {UserId} withdrew while it was starting",
                               printerId, withdrawn.FileName, withdrawer.UserId);

        return true;
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
    /// <b>The ask goes out as whoever queued the work</b>, like every other command this loop sends,
    /// on the authority the row carries - so it is asked whether or not the entry is still there. The
    /// entry can be gone: withdrawn while the start was unanswered, or dropped with a file that left
    /// the disk. Left to the elapsed-time rules, a print running with no entry was closed as
    /// <see cref="PrintState.Unknown"/> or deleted while it ran, and then its owner could not stop it
    /// and nobody heard how it ended. The entry decides only what follows: removed when the print is
    /// adopted, held beside a print nobody can describe.
    /// </para>
    /// <para>
    /// <b>A withdrawn start is asked about as whoever withdrew it.</b> Theirs is the request being
    /// carried out, and the stop that follows goes out as them anyway. The difference is the case
    /// where the queuer has since lost access - only somebody else can have withdrawn it then - and
    /// asking as the queuer would be refused until the bound closed the row with the print still
    /// running and the stop never sent. Asking reads and changes nothing; a withdrawer who may not
    /// ask could not have stopped it either.
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
                                            .SingleOrDefaultAsync(queued => queued.PrinterId == printerId &&
                                                                            queued.PrintUuid == commanded.PrintUuid,
                                                                  cancellationToken);

        bool connected = _registry.IsConnected(printerId);
        JobAnswer answer = JobAnswer.NotAsked;

        if (connected && (WithdrawerOf(commanded) ?? CallerFor(commanded)) is Caller asker && live?.JobId is int jobId)
        {
            answer = await AskWhoseJobAsync(scope, printerId, commanded, asker, entry?.FileId, jobId, cancellationToken);
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
                    "[{PrinterId}] {FileName} was printing after all - the printer took it and answered too late; " +
                    "adopting firmware job {JobId}.",
                    printerId, commanded.FileName, live?.JobId);

                commanded.State = PrintState.Starting;
                commanded.FirmwareJobId = live?.JobId;

                if (entry is not null)
                {
                    // Now, and only now, has the entry done its job. Removing it at command time is
                    // exactly what left a duplicate waiting to run.
                    dbContext.QueuedPrints.Remove(entry);
                }

                await SaveLettingWithdrawnEntriesGoAsync(dbContext, cancellationToken);

                if (entry is not null)
                {
                    await ForgetStartRefusalsAsync(dbContext, printerId, entry.FileId, cancellationToken);
                }

                break;

            case PrintStartVerdict.NeverStarted:
                _logger.LogInformation(
                    "[{PrinterId}] {FileName} never started - the printer did not take it. Still queued: {StillQueued}",
                    printerId, commanded.FileName, entry is not null);

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
    /// <b>A refusal is classified on the prose</b>, as <see cref="HandleRefusalAsync"/> is and for the
    /// same reason: these carry no machine-readable code. The wording is firmware's own, from its
    /// render fixtures, and an unrecognised one falls to
    /// <see cref="JobAnswer.Inconclusive"/> - never to a verdict, because a reason nobody has read
    /// yet must not be allowed to decide anything.
    /// </para>
    /// </remarks>
    private async Task<JobAnswer> AskWhoseJobAsync(AsyncServiceScope scope,
                                                   int printerId,
                                                   PrintJob commanded,
                                                   Caller asker,
                                                   long? printFileId,
                                                   int jobId,
                                                   CancellationToken cancellationToken)
    {
        PrinterCommandService commands = scope.ServiceProvider.GetRequiredService<PrinterCommandService>();
        CommandOutcome<JobInfoEventDataDTO>? answer;

        try
        {
            answer = await WhilePrinterAnswersAsync(scope,
                                                    () => commands.AskAsync(printerId,
                                                                            new PrusaConnect.Commands.SendJobInfo { JobId = jobId },
                                                                            asker,
                                                                            cancellationToken));
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException or
                                      CommandResponseTimedOutException or CommandSendTimedOutException or
                                      TeamAccessDeniedException or CredentialScopeDeniedException or
                                      CommandAnswerUnreadableException)
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

        JobInfoEventDataDTO? job = answer?.Answer;

        if (job is null || (job.Path is null && job.DisplayName is null))
        {
            // A job the printer only remembers renders its state and nothing else - FIN_OK, or
            // FIN_STOPPED. There is no name in it to compare, so it settles nothing.
            return JobAnswer.Inconclusive;
        }

        // The name the printer knows the file by is the one it was sent under, which may carry its
        // owner's name; the record keeps the file's own, which is what history shows. Only the entry
        // says which file that was, so a withdrawn one is matched on the path and the file's own name
        // alone - and the path is what START_PRINT was given, which the printer echoes.
        string? driveName = printFileId is long fileId ?
            await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                       .FilesOnPrinters
                       .Where(row => row.PrinterId == printerId && row.FileId == fileId)
                       .Select(row => row.DriveName)
                       .FirstOrDefaultAsync(cancellationToken) :
            null;

        bool ours = (job.Path is not null && job.Path == commanded.PrinterPath) ||
                    (job.DisplayName is not null &&
                     (job.DisplayName == commanded.FileName || job.DisplayName == driveName));

        if (!ours)
        {
            _logger.LogInformation(
                "[{PrinterId}] firmware job {JobId} is {TheirPath}, not the {OurPath} we asked for; " +
                "the print running here is not ours.",
                printerId, jobId, ForLog(job.Path ?? job.DisplayName), ForLog(commanded.PrinterPath));
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
        if (job.FirmwareJobId is not int jobId)
        {
            return null;
        }

        Caller? recorded = CallerFor(job);

        if (recorded is null)
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
            answer = await WhilePrinterAnswersAsync(scope,
                                                    () => commands.AskAsync(printerId,
                                                                            new PrusaConnect.Commands.SendJobInfo { JobId = jobId },
                                                                            recorded,
                                                                            cancellationToken));
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException or
                                      CommandResponseTimedOutException or CommandSendTimedOutException or
                                      TeamAccessDeniedException or CredentialScopeDeniedException or
                                      CommandAnswerUnreadableException)
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
    /// <para>
    /// The row is closed <see cref="PrintState.Unknown"/>, which is what that state is for - it
    /// stopped being observable without saying how - and the hold is what keeps the entry from being
    /// printed a second time on the strength of not knowing. Both, because either alone is wrong:
    /// closing without holding advances the queue onto a print that may already have run, and
    /// holding without closing leaves the printer's one open-print slot occupied for ever.
    /// </para>
    /// <para>
    /// <b>A withdrawn start is not stopped here either</b>, and the row's reason says so. Nobody has
    /// established that what the printer is running is ours, and a stop sent on that would end
    /// whatever it is - somebody's print started at the panel included.
    /// </para>
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
            "[{PrinterId}] gave up asking whether {FileName} started: the printer reports a job it will not " +
            "describe. Holding the queue - printing it again might print it twice.",
            printerId, commanded.FileName);

        // Same reasoning as the backstop: this row closes regardless, so ask before recording a
        // guess. The hold below is unaffected either way - knowing how a print ended does not say
        // whether the entry beside it is safe to run again.
        PrintState settled = await AskPriorOutcomeAsync(scope, printerId, commanded, cancellationToken) ??
                             PrintState.Unknown;

        commanded.Reason = (settled, commanded.WithdrawnByUserId) switch
        {
            (PrintState.Unknown, null) => "The printer never said whether it started this print.",
            (PrintState.Unknown, _) => "Withdrawn while it was starting. The printer never said whether it had started, " +
                                       "so it was not stopped.",
            (_, null) => "The printer would not describe this print while it ran, and reported afterwards how it ended.",
            _ => "Withdrawn while it was starting. The printer would not describe it while it ran, so it was not " +
                 "stopped, and reported afterwards how it ended.",
        };

        Close(commanded, settled, now);

        if (entry is not null)
        {
            FileOnPrinter? onPrinter = await dbContext.FilesOnPrinters
                                                      .SingleOrDefaultAsync(
                                                          row => row.PrinterId == printerId &&
                                                                 row.FileId == entry.FileId,
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

    /// <summary>
    /// Asks the printer what it calls a file that has arrived and was never named, and records the
    /// answer - or holds the queue once asking has gone on for <see cref="PathUnresolvableAfter"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The name is the printer's to give.</b> A print must be started by the 8.3 alias the drive
    /// assigned, which depends on what else is on it; firmware's <c>FILE_INFO</c> answer to
    /// <c>SEND_FILE_INFO</c> carries exactly that, asked about by the long name the file was sent
    /// under - the question <see cref="QueueTransferPolicy"/> already asks of a file it finds there.
    /// </para>
    /// <para>
    /// <b><c>File not found</c> is the drive correcting us</b>, as it is for a refused start: the
    /// belief is cleared and the file sent again. Anything else that is not a name - no answer, a
    /// refusal nobody has read, an answer without a path - is asked again after
    /// <see cref="BlockRecheckAfter"/>, until the bound.
    /// </para>
    /// </remarks>
    private async Task AskForPrinterPathAsync(AsyncServiceScope scope,
                                              HomespoolDbContext dbContext,
                                              int printerId,
                                              QueuedPrint head,
                                              CancellationToken cancellationToken)
    {
        FileOnPrinter? onPrinter = await dbContext.FilesOnPrinters
                                                  .SingleOrDefaultAsync(row => row.PrinterId == printerId &&
                                                                               row.FileId == head.FileId,
                                                                        cancellationToken);

        if (onPrinter?.ArrivedAt is not DateTimeOffset arrivedAt || onPrinter.PrinterPath is not null)
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (now - arrivedAt >= PathUnresolvableAfter)
        {
            new QueueHolds(_timeProvider, _logger).HoldPathUnknown(dbContext, printerId, head, onPrinter);
            await dbContext.SaveChangesAsync(cancellationToken);

            return;
        }

        if (now - arrivedAt < PathAskAfter ||
            (_pathAsked.TryGetValue(printerId, out (long row, DateTimeOffset at) asked) &&
             asked.row == onPrinter.Id &&
             now - asked.at < BlockRecheckAfter))
        {
            return;
        }

        _pathAsked[printerId] = (onPrinter.Id, now);

        string driveName = onPrinter.DriveName ?? head.File!.Name;
        PrinterCommandService commands = scope.ServiceProvider.GetRequiredService<PrinterCommandService>();
        CommandOutcome<FileInfoEventDataDTO>? answer;

        try
        {
            answer = await WhilePrinterAnswersAsync(scope,
                                                    () => commands.AskAsync(printerId,
                                                                            new PrusaConnect.Commands.SendFileInfo
                                                                            {
                                                                                Path = PrinterDriveNames.OnDrive(driveName),
                                                                            },
                                                                            CallerFor(head),
                                                                            cancellationToken));
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException or
                                      CommandResponseTimedOutException or CommandSendTimedOutException or
                                      TeamAccessDeniedException or CredentialScopeDeniedException or
                                      CommandAnswerUnreadableException)
        {
            _logger.LogDebug(e, "[{PrinterId}] could not ask what {FileName} is called on the drive", printerId, driveName);

            return;
        }

        if (answer?.EventType is PrinterEventType.Rejected or PrinterEventType.Failed)
        {
            if (answer.Reason == PrinterDriveCopies.NotFound)
            {
                _logger.LogInformation("[{PrinterId}] the drive does not have {FileName} after all; sending it again",
                                       printerId, driveName);

                dbContext.FilesOnPrinters.Remove(onPrinter);
                await dbContext.SaveChangesAsync(cancellationToken);

                return;
            }

            _logger.LogDebug("[{PrinterId}] the printer would not say what {FileName} is called: {Reason}",
                             printerId, driveName, ForLog(answer.Reason));

            return;
        }

        if (answer?.Answer?.Path is not string path)
        {
            _logger.LogDebug("[{PrinterId}] the printer described {FileName} without a path", printerId, driveName);

            return;
        }

        onPrinter.PrinterPath = path;
        await dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("[{PrinterId}] {FileName} is on the drive as {PrinterPath}, by asking",
                               printerId, driveName, ForLog(path));
    }

    /// <summary>Has the head's file sent to the printer, under the queue's policy.</summary>
    /// <remarks>
    /// What the queue decides - whether there is a file, whether it fits, what a refusal means - is
    /// <see cref="QueueTransferPolicy"/>'s, run on the printer's transfer mailbox. Only failures that
    /// say nothing about the file come back here, to be logged and tried again on the next pass.
    /// </remarks>
    private async Task TransferAsync(QueuePassWork work, int printerId, QueuedPrint head, CancellationToken cancellationToken)
    {
        QueueTransferPolicy policy = new(head.Id, _timeProvider, _logger);
        string fileName = head.File!.Name;

        try
        {
            await work.WhilePrinterAnswersAsync(() => _transfers.SendAsync(new TransferRequest(printerId, head.FileId, CallerFor(head), policy),
                                                                           cancellationToken));
        }
        catch (CommandAlreadyInFlightException) when (!policy.Begun)
        {
            // Refused before anything was decided: another send to this printer - a person's - is
            // waiting or under way, and the printer has one transfer slot. Tried again on the next pass.
            _logger.LogDebug("[{PrinterId}] another send to the printer is under way; {FileName} waits for the next pass",
                             printerId, fileName);
        }
        catch (Exception e) when (!policy.ReachedTheOffer &&
                                  e is PrinterNotConnectedException or CommandAlreadyInFlightException or
                                      CommandResponseTimedOutException or CommandSendTimedOutException or
                                      TeamAccessDeniedException or CredentialScopeDeniedException)
        {
            // Not an answer about the copy. A delete that did land is found gone on the next pass,
            // which asks again and is told "File not found".
            _logger.LogDebug(e, "[{PrinterId}] could not delete the older copy of {FileName}", printerId, fileName);
        }
        catch (CommandResponseTimedOutException)
        {
            // Not an answer, and the stamp stays: as likely a transfer running as one that never
            // began. The snapshot settles it by observation - the printer reporting the file, or the
            // offer going uncollected.
            _logger.LogInformation(
                "[{PrinterId}] the printer did not answer the offer of {FileName} in time; waiting for it to report the transfer",
                printerId, fileName);
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException or
                                      CommandSendTimedOutException)
        {
            // Cleared, and not because the command is known to have failed: FileSender revoked the
            // offer, so a printer that did take it can fetch nothing and firmware abandons the
            // download. No transfer of these bytes can be running, and the next pass offers them again.
            _logger.LogInformation(e, "[{PrinterId}] could not start the transfer of {FileName}", printerId, fileName);
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
                               printerId, fileName);
        }
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
    /// <see cref="FileOnPrinter.TransferStartedAt"/> is written early to close.
    /// </para>
    /// <para>
    /// <b>The queue entry stays until the printer confirms.</b> Removing it on a command that may not
    /// have landed would trade this defect for its mirror image - a queued print silently dropped
    /// because a printer was slow to answer.
    /// </para>
    /// <para>
    /// <b>And the entry is looked for again after the row is written, before anything is sent.</b>
    /// It can be withdrawn at any point in a pass, and a withdrawal deletes the entry and then marks
    /// any open row for it to be stopped - so with the row written first, either this finds the entry
    /// gone and sends nothing, or the withdrawal finds the row and the print is stopped once it is
    /// known to be running. One withdrawn after this point is the same second case, which is why the
    /// confirmation's save lets an entry already gone go rather than undoing the start with it.
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
            PrintUuid = head.PrintUuid,
            FileName = head.File!.Name,
            Digest = head.File.Digest,
            QueuedByUserId = head.QueuedByUserId,
            QueuedByScope = head.QueuedByScope,
            PrinterPath = printerPath,
            StartedAt = now,
            CommandedAt = now,
            State = PrintState.Unconfirmed,
        };

        dbContext.PrintJobs.Add(commanded);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (!await dbContext.QueuedPrints.AnyAsync(queued => queued.Id == head.Id, cancellationToken))
        {
            _logger.LogInformation("[{PrinterId}] {FileName} was withdrawn as it was about to start; not starting it.",
                                   printerId, commanded.FileName);

            dbContext.PrintJobs.Remove(commanded);
            await dbContext.SaveChangesAsync(cancellationToken);

            return;
        }

        try
        {
            CommandOutcome? outcome = await WhilePrinterAnswersAsync(scope,
                                                                     () => commands.SendCommandAsync(printerId,
                                                                                                     new StartPrint(printerPath),
                                                                                                     CallerFor(head),
                                                                                                     cancellationToken));

            if (outcome?.EventType is PrinterEventType.Rejected or PrinterEventType.Failed)
            {
                await HandleRefusalAsync(printerId, dbContext, head, commanded, outcome.Reason, cancellationToken);
                await SaveLettingWithdrawnEntriesGoAsync(dbContext, cancellationToken);

                return;
            }

            _logger.LogInformation("[{PrinterId}] started printing {Path}", printerId, ForLog(printerPath));

            // Starting rather than Printing: the printer has accepted the command and will keep
            // reporting READY for a few seconds yet. ReconcilePrintAsync promotes it when telemetry
            // says otherwise, and that is also where the firmware job id is picked up.
            commanded.State = PrintState.Starting;

            // The entry has done its job; the history row carries it from here - and if it was
            // withdrawn while the printer was answering, the row also carries the request to stop it.
            dbContext.QueuedPrints.Remove(head);
            await SaveLettingWithdrawnEntriesGoAsync(dbContext, cancellationToken);
            await ForgetStartRefusalsAsync(dbContext, printerId, head.FileId, cancellationToken);
        }
        catch (Exception e) when (e is CommandAlreadyInFlightException or TeamAccessDeniedException or
                                      CredentialScopeDeniedException)
        {
            // The three refusals that happen before anything is written to a socket: the in-flight
            // slot is taken, the team says no, the credential says no. Each is a statement that this
            // command did not reach the printer, so the row is removed rather than left as a question
            // nobody needs to answer.
            _logger.LogInformation(e, "[{PrinterId}] did not send a print of {Path}", printerId, ForLog(printerPath));
            dbContext.PrintJobs.Remove(commanded);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception e) when (e is CommandResponseTimedOutException or CommandSendTimedOutException or
                                      PrinterNotConnectedException)
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
                               printerId, ForLog(printerPath));
        }
    }

    /// <summary>
    /// Saves, letting go of a queue entry somebody withdrew in the meantime rather than undoing the
    /// rest of the save with it.
    /// </summary>
    /// <remarks>
    /// <b>Removing an entry that is already gone matches no row</b>, which EF reports as a concurrency
    /// failure and answers by rolling back the whole save - taking with it the print row this save was
    /// settling, which is what left a running print recorded as a question nobody went on to ask. The
    /// removal is the one write here that somebody else has already made, so it is dropped and the
    /// rest saved again. A failure on anything else is still thrown.
    /// </remarks>
    private static async Task SaveLettingWithdrawnEntriesGoAsync(HomespoolDbContext dbContext,
                                                                 CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException e) when (e.Entries.All(entry => entry.Entity is QueuedPrint &&
                                                                          entry.State == EntityState.Deleted))
        {
            foreach (EntityEntry withdrawn in e.Entries)
            {
                withdrawn.State = EntityState.Detached;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Forgets the refused starts counted against a file on this printer, once the printer has taken
    /// a print of it.
    /// </summary>
    /// <remarks>
    /// <b>A statement of its own, after the save that records the print</b>, rather than a change
    /// tracked into it: that save is what records a print the printer has taken, and a row gone from
    /// under this one - its file sent again, or found missing - must not be able to undo it.
    /// </remarks>
    private static Task ForgetStartRefusalsAsync(HomespoolDbContext dbContext,
                                                 int printerId,
                                                 long fileId,
                                                 CancellationToken cancellationToken)
    {
        return dbContext.FilesOnPrinters
                        .Where(row => row.PrinterId == printerId &&
                                      row.FileId == fileId &&
                                      row.StartRefusalCount != null)
                        .ExecuteUpdateAsync(set => set.SetProperty(row => row.StartRefusalCount, (int?)null)
                                                      .SetProperty(row => row.StartRefusedAt, (DateTimeOffset?)null)
                                                      .SetProperty(row => row.StartRefusalReason, (string?)null),
                                            cancellationToken);
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
    /// <b>Waiting is bounded, though.</b> A transient answer, or one nobody has read, is retried on
    /// <see cref="RefusalRetries"/>' waits, and the same answer
    /// <see cref="RefusalRetries.HoldAfter"/> times running holds the queue as
    /// <see cref="PrintHoldReason.PrintRefused"/>. The queue commands only a printer reporting
    /// <c>READY</c>, so a printer still saying <c>Can't print now</c> after minutes of that disagrees
    /// with its own report - and one whose <c>print_begin</c> fails on a broken file says it every time.
    /// </para>
    /// <para>
    /// <b>Every arm here settles <paramref name="commanded"/> except one, because a refusal is an
    /// answer - except one.</b> The row was opened before the command went out to survive the case
    /// where no answer comes at all; once the printer has said <i>no</i>, nothing is outstanding. A
    /// terminal refusal closes it as the failed print it is, and a transient one removes it - a row
    /// per retry would turn history into a log of a printer repeating itself - until the refusal that
    /// holds the queue, which closes it as failed too. The exception is <c>No job in progress</c>,
    /// which is not the printer saying no: see that arm.
    /// </para>
    /// </remarks>
    private async Task HandleRefusalAsync(int printerId,
                                          HomespoolDbContext dbContext,
                                          QueuedPrint head,
                                          PrintJob commanded,
                                          string? reason,
                                          CancellationToken cancellationToken)
    {
        switch (reason)
        {
            case "File not found":
                _logger.LogInformation("[{PrinterId}] the drive no longer has {FileName}; sending it again",
                                       printerId, head.File?.Name);

                await dbContext.FilesOnPrinters
                               .Where(row => row.PrinterId == printerId && row.FileId == head.FileId)
                               .ExecuteDeleteAsync(cancellationToken);

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
                    printerId, ForLog(commanded.PrinterPath));
                break;

            case "Forbidden path":
            case "Tools mapping not enabled":
                _logger.LogError(
                    "[{PrinterId}] refused {FileName} with \"{Reason}\", which will not change by retrying; " +
                    "removing it from the queue.",
                    printerId, head.File?.Name, reason);

                // Recorded as a failed print rather than only logged. Dropping the entry with nothing
                // to show for it is how a queued print used to vanish with no way for its owner to
                // find out why. It spans the ask and nothing else, which is honest: nothing printed.
                commanded.Reason = reason;
                Close(commanded, PrintState.Failed, _timeProvider.GetUtcNow());

                dbContext.QueuedPrints.Remove(head);
                break;

            default:
                // "Can't print now", and anything a future firmware adds. Treating an unrecognised
                // reason as terminal would throw away a print for a string nobody has read yet, so it
                // is retried - counted, spaced, and held once the same answer has come back too often.
                FileOnPrinter? onPrinter = await dbContext.FilesOnPrinters
                                                          .SingleOrDefaultAsync(
                                                              row => row.PrinterId == printerId &&
                                                                     row.FileId == head.FileId,
                                                              cancellationToken);

                if (onPrinter is not null &&
                    new QueueHolds(_timeProvider, _logger).RecordStartRefusal(printerId, head, onPrinter, reason))
                {
                    // The hold's one history row is this print, refused, in the printer's words - and
                    // the entry stays, held, for a person to cancel or queue again.
                    commanded.Reason = onPrinter.StartRefusalReason;
                    Close(commanded, PrintState.Failed, _timeProvider.GetUtcNow());
                    break;
                }

                if (onPrinter is null)
                {
                    // Gone since the pass read it, so there is nothing to count against; the next
                    // pass finds the file missing and sends it again.
                    _logger.LogDebug("[{PrinterId}] not printing yet: {Reason}", printerId, ForLog(reason));
                }

                dbContext.PrintJobs.Remove(commanded);
                break;
        }
    }
}
