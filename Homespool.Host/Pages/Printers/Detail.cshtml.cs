using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

using Homespool.Host.Authorisation;
using Homespool.Host.Cameras;
using Homespool.Host.Exceptions;
using Homespool.Host.Localisation;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect;
using Homespool.Host.Queue;
using Homespool.Host.Services;
using Homespool.Host.Telemetry;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Printers;

/// <summary>
/// One printer's state, its queue, and the controls that change what it is doing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The page is arranged around one question - what is this printer doing right now.</b> The
/// status card answers it; everything that is a record rather than a state (finished prints, the
/// event log) is behind a disclosure, because a page whose height is dominated by history buries the
/// thing somebody opened it for.
/// </para>
/// <para>
/// <b>The queue lives here rather than on a page of its own</b> because it belongs to one printer and
/// reads as part of its status: what it is doing, then what it will do next. Reordering and
/// cancelling are edits to a list, and the producer loop is what turns an entry into a transfer and
/// a print later.
/// </para>
/// <para>
/// <b>The queue's own buttons send nothing; the control strip does</b> - pause, resume, stop,
/// preheat, cool down and Set ready. The distinction worth keeping is not page-versus-service but
/// queue-versus-printer: an edit to the list changes what will happen, and the strip changes what the
/// machine is doing now.
/// </para>
/// <para>
/// <b><see cref="OnGetStatusAsync"/> is the same card, alone, for the poll that keeps it current</b> -
/// see its own remarks for why the answer is rendered HTML rather than JSON.
/// </para>
/// </remarks>
[Authorize]
public class DetailModel : PageModel
{
    private readonly PrinterQueryService _printerQueryService;
    private readonly PrintQueueService _queueService;
    private readonly PrinterPreheatService _preheat;
    private readonly PrintHistoryService _historyService;
    private readonly QueueSnapshotReader _snapshots;
    private readonly PrinterAccessService _access;
    private readonly CameraAccessService _cameraAccess;
    private readonly CameraDisplayNames _cameraNames;
    private readonly PrinterConnectionRegistry _connectionRegistry;
    private readonly PrinterCommandService _commands;
    private readonly PrintStopService _stops;
    private readonly PrinterStatusText _statusText;
    private readonly PrinterIntentText _intents;
    private readonly RelativeTimeText _ages;
    private readonly TimeProvider _timeProvider;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ErrorText _errors;
    private readonly UserManager<HSUser> _userManager;

    public DetailModel(PrinterQueryService printerQueryService,
                       PrintQueueService queueService,
                       PrinterPreheatService preheat,
                       PrintHistoryService historyService,
                       QueueSnapshotReader snapshots,
                       PrinterAccessService access,
                       CameraAccessService cameraAccess,
                       CameraDisplayNames cameraNames,
                       PrinterConnectionRegistry connectionRegistry,
                       PrinterCommandService commands,
                       PrintStopService stops,
                       PrinterStatusText statusText,
                       PrinterIntentText intents,
                       RelativeTimeText ages,
                       TimeProvider timeProvider,
                       IStringLocalizer<SharedResource> localiser,
                       ErrorText errors,
                       UserManager<HSUser> userManager)
    {
        _printerQueryService = printerQueryService;
        _queueService = queueService;
        _preheat = preheat;
        _historyService = historyService;
        _snapshots = snapshots;
        _access = access;
        _cameraAccess = cameraAccess;
        _cameraNames = cameraNames;
        _connectionRegistry = connectionRegistry;
        _commands = commands;
        _stops = stops;
        _statusText = statusText;
        _intents = intents;
        _ages = ages;
        _timeProvider = timeProvider;
        _localiser = localiser;
        _errors = errors;
        _userManager = userManager;
    }

    public PrinterStatistics Statistics { get; private set; } = null!;

    public bool Connected { get; private set; }

    /// <summary>What this printer will print, in order. Empty until somebody queues something.</summary>
    public IReadOnlyList<QueuedPrint> Queue { get; private set; } = [];

    /// <summary>The print running now, or null. A row still <c>Starting</c> counts - it has begun.</summary>
    public PrintJob? ActivePrint { get; private set; }

    /// <summary>Finished prints, newest first.</summary>
    public IReadOnlyList<PrintJob> History { get; private set; } = [];

    /// <summary>Usernames for whoever stopped any of <see cref="History"/>, keyed by user id.</summary>
    public IReadOnlyDictionary<long, string> StopperNames { get; private set; } = new Dictionary<long, string>();

    /// <summary>Who is reading the page, for the one row-level decision it has to make.</summary>
    private long _readerId;

    /// <summary>
    /// Whether this finished print may be run again from here.
    /// </summary>
    /// <remarks>
    /// <b>Your own prints only</b> - see <see cref="OnPostReprintAsync"/> for why that is about
    /// printing the right file rather than about permission. <see cref="CanUse"/> as well, because
    /// reprinting is still queueing, and somebody who may only watch this printer may not.
    /// </remarks>
    public bool CanReprint(PrintJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return CanUse && job.QueuedByUserId == _readerId;
    }

    /// <summary>The nozzle, and whether it is climbing towards a setpoint or sitting on one.</summary>
    public HeaterReading Nozzle { get; private set; } = new(null, null, HeaterState.Unknown);

    /// <summary>The bed, likewise.</summary>
    public HeaterReading Bed { get; private set; } = new(null, null, HeaterState.Unknown);

    /// <summary>The temperature graph, or null when the printer reported nothing in the window.</summary>
    public TemperatureChart? Chart { get; private set; }

    /// <summary>The window <see cref="Chart"/> covers, for the caption that says what is being shown.</summary>
    public TimeSpan ChartWindow { get; private set; }

    /// <summary>
    /// Whether <see cref="ChartWindow"/> is the running print's own length rather than the idle
    /// default, so the caption can say which.
    /// </summary>
    public bool ChartFollowsJob { get; private set; }

    /// <summary>
    /// How a stopped print says who stopped it - the qualifier after the outcome, never on its own.
    /// </summary>
    /// <remarks>
    /// <b>Three cases, and the middle one is why this is not a boolean.</b> A null
    /// <see cref="PrintJob.StoppedByUserId"/> means the panel; an id we can name means a person here;
    /// an id we cannot means a person here whose account is no longer readable, which is worth saying
    /// out loud rather than quietly rendering as the panel.
    /// </remarks>
    public string StoppedByDescription(PrintJob job)
    {
        if (job.StoppedByUserId is not { } stopper)
        {
            return _localiser["Printers_StoppedAtPrinter"];
        }

        return StopperNames.TryGetValue(stopper, out string? name) ?
            _localiser["Printers_StoppedByPerson", name] :
            _localiser["Printers_StoppedFromHere"];
    }

    /// <summary>What the printer's status is called, in the reader's language.</summary>
    public string StatusWord(PrinterStatus? status)
    {
        return _statusText.For(status);
    }

    /// <summary>
    /// How long ago the last telemetry landed, in words.
    /// </summary>
    /// <remarks>
    /// <b>The card's honesty check.</b> It refreshes itself, so without an age on it a printer that
    /// stopped answering four minutes ago is indistinguishable from one answering now - the exact
    /// confusion a live view is meant to remove.
    /// </remarks>
    public string LastSeenDescription(DateTimeOffset at)
    {
        return _ages.Since(at, _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Why the queue is held, or null. <b>The reason this page needed history at all</b> - the loop
    /// can hold indefinitely on a full drive, and until something showed this, a hold was
    /// indistinguishable from a queue that had stopped for no reason.
    /// </summary>
    public string? HoldReason { get; private set; }

    /// <summary>
    /// Which hold it is, so the page can offer advice that fits it.
    /// </summary>
    /// <remarks>
    /// <b>The sentence alone is not enough to advise on.</b> "Free some space and it will carry on"
    /// is right for a full drive and actively misleading for a file whose nozzle does not match -
    /// deleting things would achieve nothing there. The holds that are not about space carry their
    /// own advice in their own sentence, so this only has to recognise the one that does not.
    /// </remarks>
    public PrintHoldReason? HoldKind { get; private set; }

    /// <summary>
    /// What the queue is waiting on, or null when nothing needs saying.
    /// </summary>
    /// <remarks>
    /// Computed rather than stored: the same snapshot the loop reads, put through the same rules, so
    /// the page cannot state something the loop does not believe. Null where another part of the page
    /// already covers it - see <see cref="QueueWaitDescription"/>.
    /// </remarks>
    public string? WaitingOn { get; private set; }

    /// <summary>
    /// Which wait it is, so the page can treat the one a person has to clear differently from the
    /// ones the loop clears by itself.
    /// </summary>
    /// <remarks>
    /// <b>The distinction the sentence alone could not make, and it cost somebody an evening</b>
    /// (Henrik, 2026-08-21, dogfooding): a queued print had not started, the printer was <c>Idle</c>
    /// rather than <c>Ready</c>, and the explanation was on the page as grey footnote text below the
    /// temperature tiles. <c>Transferring</c> and <c>AwaitingPrinterPath</c> are the loop working and
    /// deserve a footnote; <see cref="QueueWaitReason.PrinterNotAvailable"/> is the queue waiting on a
    /// person and needs to look like it.
    /// </remarks>
    public QueueWaitReason? WaitingReason { get; private set; }

    /// <summary>
    /// Whether the queue is stopped on something only a person can clear.
    /// </summary>
    /// <remarks>
    /// The knowledge lives with the sentences rather than here - see
    /// <see cref="QueueWaitDescription.NeedsAPerson"/>, which is where the reasons are already sorted
    /// into those the loop clears and those it does not.
    /// </remarks>
    public bool WaitingOnAPerson => QueueWaitDescription.NeedsAPerson(WaitingReason);

    /// <summary>
    /// Whether the caller may change the queue, which decides whether the controls render at all.
    /// </summary>
    /// <remarks>
    /// Rendering is not the enforcement - <see cref="PrintQueueService"/> is, and it checks again on
    /// every post. This only keeps buttons off a page where pressing them could only fail.
    /// </remarks>
    public bool CanUse { get; private set; }

    /// <summary>
    /// Whether the caller may change this printer's settings - today, only whether it can be marked
    /// ready from here.
    /// </summary>
    /// <remarks>
    /// <b>A different permission from <see cref="CanUse"/>, and deliberately so.</b> Pressing Set
    /// ready is a printer control; deciding that pressing it is honest for this machine is a standing
    /// judgement about the machine, which is <c>CanManage</c>'s business. So somebody who has stood in
    /// the garage decides once, and members who never have inherit that decision rather than being
    /// asked to make it about a printer they cannot see.
    /// </remarks>
    public bool CanManage { get; private set; }

    /// <summary>
    /// The address to paste into a slicer's print-host field for this printer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built from the live request rather than from configuration, so it is right behind a reverse
    /// proxy and on a non-standard port with nothing to keep in step. <c>Request.Host</c> carries the
    /// port because the proxy forwards <c>$http_host</c> rather than <c>$host</c> - which strips it,
    /// and which cost a session once already (<c>notes/tls-by-default.md</c>).
    /// </para>
    /// <para>
    /// The trailing slash is deliberate: the slicer appends <c>api/version</c> to whatever it is
    /// given, and a value pasted without one still works only because <c>make_url</c> repairs it.
    /// Handing over the repaired form means never depending on that.
    /// </para>
    /// </remarks>
    public string SlicerUrl { get; private set; } = string.Empty;

    /// <summary>
    /// The filament presets this printer offers, for the preheat control.
    /// </summary>
    /// <remarks>
    /// Model-dependent, which is not cosmetic: a MINI's PA target differs because its maximum nozzle
    /// temperature is lower, and 285 is a target it would refuse.
    /// </remarks>
    public IReadOnlyList<FilamentPreset> Presets { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public bool StatusSuccess { get; set; }

    /// <summary>
    /// The cameras watching this printer that the caller may see. Empty is the ordinary case.
    /// </summary>
    public IReadOnlyList<Camera> Cameras { get; private set; } = [];

    /// <summary>
    /// The caption under a picture — see <see cref="CameraDisplayNames"/>.
    /// </summary>
    /// <remarks>
    /// The position is offered as the last resort because a uuid reads badly under a photograph.
    /// It applies only to a network camera nobody has named, and is ambiguous only on a printer
    /// watched by several of them at once.
    /// </remarks>
    public string CameraName(Camera camera, int index)
    {
        return _cameraNames.For(camera, _localiser["Cameras_Numbered", index + 1]);
    }

    /// <summary>
    /// An unknown uuid and one the caller can't read both return <see cref="NotFoundResult"/> -
    /// matching <c>GetPrinterForUserAsync</c>'s "same 404 either way" rule.
    /// </summary>
    public async Task<IActionResult> OnGetAsync(Guid uuid, CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            // [Authorize] should make this unreachable; fail closed rather than act on an invented id.
            return Forbid();
        }

        Caller caller = CallerResolver.For(user, User);

        if (!await LoadStatusAsync(uuid, caller, cancellationToken))
        {
            return NotFound();
        }

        _readerId = caller.UserId;

        CanManage = await _access.AllowsAsync(Statistics.Printer.Id, caller, Capability.ManagePrinter, cancellationToken);

        SlicerUrl = $"{Request.Scheme}://{Request.Host}/compat/octoprint/{Statistics.Printer.Uuid}/";

        Presets = FilamentPreset.For(Statistics.Printer.Model);

        Queue = await _queueService.ListAsync(Statistics.Printer.Id, caller, cancellationToken);
        History = await _historyService.ListAsync(Statistics.Printer.Id, caller, cancellationToken);
        StopperNames = await _historyService.GetStopperNamesAsync(History, cancellationToken);

        Cameras = await _cameraAccess.ListForPrinterAsync(Statistics.Printer.Id, caller, cancellationToken);

        await LoadChartAsync(uuid, caller, cancellationToken);

        return Page();
    }

    /// <summary>
    /// The status card on its own, for the poll that keeps it current.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It answers with rendered HTML rather than JSON, and that is the load-bearing choice.</b>
    /// Every word on the card is localised and every number is culture-formatted - a comma decimal
    /// separator in <c>da</c>, a temperature widened by SQLite that has to be narrowed before it is
    /// printed (<c>notes/floating-point.md</c>). Answering with JSON would mean a second
    /// implementation of all of that in JavaScript, kept in step by hand, with the resource files
    /// unable to see it. Rendering it here means the poll costs one partial and no vocabulary at all
    /// on the client.
    /// </para>
    /// <para>
    /// <b>The control strip is deliberately not in this partial.</b> It carries a filament
    /// <c>select</c>, and replacing the markup underneath somebody every two seconds would reset
    /// their choice mid-press. Controls change what the printer does; this changes what the page
    /// says.
    /// </para>
    /// </remarks>
    public async Task<IActionResult> OnGetStatusAsync(Guid uuid, CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return Forbid();
        }

        if (!await LoadStatusAsync(uuid, CallerResolver.For(user, User), cancellationToken))
        {
            return NotFound();
        }

        return Partial("_PrinterStatus", this);
    }

    /// <summary>
    /// The temperature graph on its own, on a slower poll than the card above it.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="OnGetStatusAsync"/> because it is a different cost and a different
    /// rate: a day-long window aggregates a lot of rows, and a graph of a print redrawn every two
    /// seconds would look identical each time.
    /// </remarks>
    public async Task<IActionResult> OnGetGraphAsync(Guid uuid, CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return Forbid();
        }

        Caller caller = CallerResolver.For(user, User);

        if (!await LoadStatusAsync(uuid, caller, cancellationToken))
        {
            return NotFound();
        }

        await LoadChartAsync(uuid, caller, cancellationToken);

        return Partial("_TemperatureGraph", this);
    }

    /// <summary>Everything the status card shows. Shared by the page and its poll.</summary>
    /// <returns>False when there is no such printer, or none this caller may read.</returns>
    private async Task<bool> LoadStatusAsync(Guid uuid, Caller caller, CancellationToken cancellationToken)
    {
        PrinterStatistics? statistics =
            await _printerQueryService.GetPrinterStatisticsForUserAsync(uuid, caller, cancellationToken);

        if (statistics is null)
        {
            return false;
        }

        Statistics = statistics;
        Connected = _connectionRegistry.IsConnected(statistics.Printer.Id);

        CanUse = await _access.AllowsAsync(statistics.Printer.Id, caller, Capability.Print, cancellationToken);

        Nozzle = HeaterReading.For(statistics.LiveState?.NozzleTemperature, statistics.LiveState?.TargetNozzleTemperature);
        Bed = HeaterReading.For(statistics.LiveState?.BedTemperature, statistics.LiveState?.TargetBedTemperature);

        ActivePrint = await _historyService.GetActiveAsync(statistics.Printer.Id, caller, cancellationToken);

        // Both of these arrive as keys and are said here, which is the only place that knows who is
        // reading. The loop that recorded the hold had no request to take a culture from.
        MessageKey? hold = await _historyService.GetHoldReasonAsync(statistics.Printer.Id, caller, cancellationToken);

        HoldReason = hold is null ? null : _errors.For(hold);

        QueueSnapshot snapshot = await _snapshots.ReadAsync(statistics.Printer.Id, cancellationToken);
        QueueAction decision = QueueRules.Decide(snapshot);
        MessageKey? waiting = QueueWaitDescription.For(decision, snapshot.Head?.FileName);

        HoldKind = snapshot.HoldReason;

        WaitingReason = decision.Kind == QueueActionKind.Wait ? decision.Reason : null;
        WaitingOn = waiting is null ? null : _errors.For(waiting);

        return true;
    }

    /// <summary>Reads the temperature window and works out its drawing.</summary>
    private async Task LoadChartAsync(Guid uuid, Caller caller, CancellationToken cancellationToken)
    {
        (DateTimeOffset from, DateTimeOffset to) = TemperatureWindow.For(Statistics.LiveState, _timeProvider.GetUtcNow());

        ChartWindow = to - from;
        ChartFollowsJob = Statistics.LiveState?.TimePrinting is > 0;

        TemperatureSeries? series =
            await _printerQueryService.GetTemperatureSeriesAsync(uuid, caller, from, to, cancellationToken);

        Chart = series is null ? null : TemperatureChart.For(series);
    }

    /// <summary>Moves a queued print to a new position.</summary>
    /// <remarks>
    /// Takes the target index rather than a direction, because that is what the service takes and
    /// where the clamping lives - the page only does the arithmetic its two buttons imply.
    /// </remarks>
    public Task<IActionResult> OnPostMoveAsync(Guid uuid,
                                               Guid id,
                                               int position,
                                               CancellationToken cancellationToken)
    {
        return ActAsync(uuid, async (caller, printer) =>
        {
            bool moved = await _queueService.MoveAsync(id, caller, position, cancellationToken);

            return moved ?
                (_localiser["Printers_QueueReordered"].Value, true) :
                (_localiser["Printers_JobGone"].Value, false);
        }, cancellationToken);
    }

    /// <summary>
    /// Cancels a queued print. Never stops a print that has already started - see
    /// <see cref="PrintQueueService.CancelAsync"/>.
    /// </summary>
    public Task<IActionResult> OnPostCancelAsync(Guid uuid, Guid id, CancellationToken cancellationToken)
    {
        return ActAsync(uuid, async (caller, printer) =>
        {
            bool cancelled = await _queueService.CancelAsync(id, caller, cancellationToken);

            return cancelled ?
                (_localiser["Printers_JobRemoved"].Value, true) :
                (_localiser["Printers_JobGone"].Value, false);
        }, cancellationToken);
    }

    /// <summary>
    /// Queues one of your own finished prints again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only the person who queued a print may print it again, and that is a correctness rule
    /// rather than a permission one</b> (Henrik, 2026-08-20). The file is resolved by name in the
    /// caller's own tree - <see cref="PrintJob.FileName"/> is a record of what ran, not a pointer at
    /// it - so offering the button on somebody else's row would offer to print <em>your</em>
    /// <c>bracket.bgcode</c> under the impression you were repeating <em>theirs</em>. Two people on
    /// one team having different models under one name is ordinary, and the failure is silent: it
    /// prints, and prints the wrong thing.
    /// </para>
    /// <para>
    /// <b>The post carries the history row's handle, not a filename.</b> So this means "print that row
    /// again" rather than "queue this name", which is what the button says, and the ownership check
    /// has a row to make it against. A caller can still queue any of their own files - that is what
    /// the Files page is for - so this is not a boundary, it is the handler meaning what it says.
    /// </para>
    /// <para>
    /// It queues rather than prints. The producer loop is what turns the head of the queue into a
    /// transfer and a print, and it advances only when the printer is ready - so a reprint takes the
    /// same route as every other way a file reaches a printer.
    /// </para>
    /// </remarks>
    public Task<IActionResult> OnPostReprintAsync(Guid uuid, Guid id, CancellationToken cancellationToken)
    {
        return ActAsync(uuid, async (caller, printer) =>
        {
            PrintJob? job = await _historyService.FindAsync(printer.Id, id, caller, cancellationToken);

            if (job is null)
            {
                return (_localiser["Printers_JobGone"].Value, false);
            }

            // Re-checked rather than merely not rendered - the same rule this page states for every
            // other control. Nothing here is destructive, but a print is a physical outcome and this
            // one would be quietly the wrong file.
            if (job.QueuedByUserId != caller.UserId)
            {
                return (_localiser["Printers_ReprintNotYours"].Value, false);
            }

            try
            {
                EnqueueOutcome outcome = await _queueService.EnqueueAsync(printer.Id, caller, job.FileName, cancellationToken);

                // Queued either way - the loop is what stops a print that must not happen, and this is
                // the moment to say so while somebody is still looking at the screen. Files_Queued
                // rather than a printer-prefixed twin of it: the sentence is the same one, and a
                // second key holding it would be a second thing to translate and to let drift.
                return outcome.Warnings.Count == 0 ?
                    (_localiser["Files_Queued", job.FileName].Value, true) :
                    (string.Join(' ', outcome.Warnings.Select(_errors.For)),
                     outcome.Severity != PrintCompatibilitySeverity.Hold);
            }
            catch (PrintFileNotFoundException e)
            {
                return (_errors.For(e), false);
            }
        }, cancellationToken);
    }

    /// <summary>Pauses the running print.</summary>
    /// <remarks>
    /// <b>Gated on being connected, not on whether a print is actually running.</b> The firmware's own
    /// refusal - "No print to pause" - is the real guard and is a better sentence than any this page
    /// could compose from a status that is a second old.
    /// </remarks>
    public Task<IActionResult> OnPostPauseAsync(Guid uuid, CancellationToken cancellationToken)
    {
        return SendIntentAsync(uuid, new PausePrint(), cancellationToken);
    }

    /// <summary>Resumes a paused print.</summary>
    public Task<IActionResult> OnPostResumeAsync(Guid uuid, CancellationToken cancellationToken)
    {
        return SendIntentAsync(uuid, new ResumePrint(), cancellationToken);
    }

    /// <summary>Stops whatever this printer is running.</summary>
    /// <remarks>
    /// Through <see cref="PrintStopService"/> rather than straight to
    /// <see cref="PrinterCommandService"/>, unlike the two above it: a stop is the one whose cause the
    /// printer cannot report afterwards, so who pressed it is noted as it is sent.
    /// </remarks>
    public Task<IActionResult> OnPostStopAsync(Guid uuid, CancellationToken cancellationToken)
    {
        return SendIntentAsync(uuid, new StopPrint(), cancellationToken, _stops.StopAsync);
    }

    /// <summary>
    /// Heats nozzle and bed to the chosen filament's preset.
    /// </summary>
    /// <remarks>
    /// <b>The form posts a filament name, never a temperature.</b> The name selects a row in
    /// <see cref="FilamentPreset"/> and the row carries the numbers - which come from firmware's own
    /// table - so the page cannot ask for an arbitrary target even if the select is tampered with.
    /// An unrecognised name is a refusal rather than a fallback.
    /// </remarks>
    public Task<IActionResult> OnPostPreheatAsync(Guid uuid, string filament, CancellationToken cancellationToken)
    {
        return ActAsync(uuid, async (caller, printer) =>
        {
            FilamentPreset? preset = FilamentPreset.Find(printer.Model, filament);

            if (preset is null)
            {
                return (_localiser["Printers_NoSuchFilament", filament].Value, false);
            }

            await _preheat.PreheatAsync(printer.Id, caller, preset, cancellationToken);

            return (
                _localiser["Printers_HeatingTo", preset.NozzleTemperature, preset.BedTemperature, preset.Name].Value,
                true);
        }, cancellationToken);
    }

    /// <summary>
    /// Marks the printer ready, on the assertion made in the confirm dialog that its print sheet is
    /// clear.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The flag is re-checked here, not merely consulted when rendering.</b> A button that is not
    /// rendered is not a permission check - the same rule <see cref="ActAsync"/> states for
    /// <c>CanRead</c> - and this one guards a physical outcome rather than a page.
    /// </para>
    /// <para>
    /// <b>Nothing in the post carries the answer to the prompt.</b> There is no "I confirmed"
    /// parameter to forge, because a caller able to forge it is a caller posting directly, and for
    /// them the dialog was never the control - <see cref="Printer.RemoteReadyAllowed"/> is. The
    /// prompt's job is to make a person look, and only a person can be made to.
    /// </para>
    /// </remarks>
    public Task<IActionResult> OnPostReadyAsync(Guid uuid, CancellationToken cancellationToken)
    {
        return ActAsync(uuid, async (caller, printer) =>
        {
            if (!printer.RemoteReadyAllowed)
            {
                return (_localiser["Printers_ReadyNotAllowed"].Value, false);
            }

            await _commands.SendCommandAsync(printer.Id, new SetPrinterReady(), caller, cancellationToken);

            return (_localiser["Printers_ReadySent"].Value, true);
        }, cancellationToken);
    }

    /// <summary>Turns setting-ready-from-this-page on or off for this printer.</summary>
    /// <remarks>
    /// Through <see cref="PrinterQueryService"/> rather than writing the column here, so the
    /// <c>ManagePrinter</c> check cannot be skipped by a later caller - see
    /// <c>notes/printer-authorisation.md</c> on why the gate lives in the service.
    /// </remarks>
    public Task<IActionResult> OnPostRemoteReadyAsync(Guid uuid, bool allowed, CancellationToken cancellationToken)
    {
        return ActAsync(uuid, async (caller, printer) =>
        {
            await _printerQueryService.SetRemoteReadyAllowedAsync(printer.Uuid, caller, allowed, cancellationToken);

            return (_localiser[allowed ? "Printers_RemoteReadySaved" : "Printers_RemoteReadyCleared"].Value, true);
        }, cancellationToken);
    }

    /// <summary>Turns both heaters off.</summary>
    public Task<IActionResult> OnPostCooldownAsync(Guid uuid, CancellationToken cancellationToken)
    {
        return ActAsync(uuid, async (caller, printer) =>
        {
            await _preheat.CooldownAsync(printer.Id, caller, cancellationToken);

            return (_localiser["Printers_HeatersOff"].Value, true);
        }, cancellationToken);
    }

    /// <summary>
    /// Sends one of the three print-control intents and reports what the printer said back.
    /// </summary>
    /// <remarks>
    /// <b>The printer's own answer is reported, not merely the fact that a command left.</b> A
    /// refusal comes back as <c>Rejected</c> or <c>Failed</c> carrying firmware's wording, and
    /// swallowing it would have the page claim a stop succeeded when the printer declined it. The
    /// <paramref name="send"/> override exists for the one intent that needs more than
    /// <see cref="PrinterCommandService"/>: a stop records who asked for it as it goes.
    /// </remarks>
    private Task<IActionResult> SendIntentAsync(Guid uuid,
                                                IPrinterIntent intent,
                                                CancellationToken cancellationToken,
                                                Func<int, Caller, CancellationToken, Task<CommandOutcome?>>? send = null)
    {
        return ActAsync(uuid, async (caller, printer) =>
        {
            CommandOutcome? outcome = send is null ?
                await _commands.SendCommandAsync(printer.Id, intent, caller, cancellationToken) :
                await send(printer.Id, caller, cancellationToken);

            // Null means the command was written and no answer is expected of it. All three of these
            // are answered, so this is a guard rather than a live case.
            return outcome?.EventType switch
            {
                PrinterEventType.Rejected or PrinterEventType.Failed =>
                    (_localiser["Printers_CommandRejected", _intents.For(intent), outcome!.Reason ?? string.Empty].Value, false),
                _ => (_localiser["Printers_CommandSent", _intents.For(intent)].Value, true),
            };
        }, cancellationToken);
    }

    /// <summary>
    /// The half every handler shares: resolve the caller and the printer, run the action, and come
    /// back to the page with something to say.
    /// </summary>
    /// <remarks>
    /// The <see cref="TeamAccessDeniedException"/> arm is what a caller holding <c>CanRead</c> alone
    /// gets if they post anyway. The buttons are not rendered for them - but a button that is not
    /// rendered is not a permission check.
    /// </remarks>
    private async Task<IActionResult> ActAsync(Guid uuid,
                                               Func<Caller, Printer, Task<(string message, bool success)>> action,
                                               CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return Forbid();
        }

        // Resolved rather than trusted: this is what makes a uuid the caller cannot read a 404 here
        // as much as on the GET, instead of an id going straight to the queue service.
        Caller caller = CallerResolver.For(user, User);
        Printer? printer = await _printerQueryService.GetPrinterForUserAsync(uuid, caller, cancellationToken);

        if (printer is null)
        {
            return NotFound();
        }

        try
        {
            (StatusMessage, StatusSuccess) = await action(caller, printer);
        }
        catch (TeamAccessDeniedException)
        {
            return Forbid();
        }
        catch (PrinterBusyException e)
        {
            // Not an error page: the printer is doing something, which is an answer rather than a
            // fault, and the page is where the person already is.
            (StatusMessage, StatusSuccess) = (_errors.For(e), false);
        }
        catch (PrinterRefusedException e)
        {
            // The printer's own words. Without this the page reported success for a command the
            // printer had declined, which is worse than reporting nothing.
            (StatusMessage, StatusSuccess) = (_errors.For(e), false);
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException
                                      or CommandResponseTimedOutException or CommandSendTimedOutException)
        {
            (StatusMessage, StatusSuccess) = (_errors.For(e), false);
        }

        return RedirectToPage(new { uuid });
    }
}
