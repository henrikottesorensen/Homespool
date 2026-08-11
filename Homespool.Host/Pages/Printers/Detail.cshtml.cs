using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Homespool.Host.Authorisation;
using Homespool.Host.Cameras;
using Homespool.Host.Exceptions;
using Homespool.Host.PrusaConnect;
using Homespool.Host.Queue;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Printers;

/// <summary>
/// One printer's live status plus recent telemetry/event history - the first reader of
/// <see cref="PrinterLiveState"/>/<see cref="Model.Entities.TelemetrySample"/>/
/// <see cref="Model.Entities.PrinterEvent"/> anywhere in the app - and its print queue.
/// </summary>
/// <remarks>
/// <b>The queue lives here rather than on a page of its own</b> because it belongs to one printer and
/// reads as part of its status: what it is doing, then what it will do next. Nothing on this page
/// sends the printer a command - reordering and cancelling are edits to a list, and the producer loop
/// is what turns an entry into a transfer and a print later.
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
            return "at the printer";
        }

        return StopperNames.TryGetValue(stopper, out string? name) ? $"by {name}" : "from here";
    }

    /// <summary>
    /// Why the queue is held, or null. <b>The reason this page needed history at all</b> - the loop
    /// can hold indefinitely on a full drive, and until something showed this, a hold was
    /// indistinguishable from a queue that had stopped for no reason.
    /// </summary>
    public string? HoldReason { get; private set; }

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
    /// Whether the caller may change the queue, which decides whether the controls render at all.
    /// </summary>
    /// <remarks>
    /// Rendering is not the enforcement - <see cref="PrintQueueService"/> is, and it checks again on
    /// every post. This only keeps buttons off a page where pressing them could only fail.
    /// </remarks>
    public bool CanUse { get; private set; }

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
    /// An unknown uuid and one the caller can't read both return <see cref="NotFoundResult"/> -
    /// matching <c>GetPrinterForUserAsync</c>'s "same 404 either way" rule.
    /// </summary>
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
        return _cameraNames.For(camera, $"Camera {index + 1}");
    }

    public async Task<IActionResult> OnGetAsync(Guid uuid, CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            // [Authorize] should make this unreachable; fail closed rather than act on an invented id.
            return Forbid();
        }

        PrinterStatistics? statistics =
            await _printerQueryService.GetPrinterStatisticsForUserAsync(uuid, user.Id, cancellationToken);

        if (statistics is null)
        {
            return NotFound();
        }

        Statistics = statistics;
        Connected = _connectionRegistry.IsConnected(statistics.Printer.Id);

        CanUse = await _access.AllowsAsync(statistics.Printer.Id, user.Id, PrinterOperation.ChangeQueue,
                                           cancellationToken);

        SlicerUrl = $"{Request.Scheme}://{Request.Host}/compat/octoprint/{statistics.Printer.Uuid}/";

        Presets = FilamentPreset.For(statistics.Printer.Model);

        Queue = await _queueService.ListAsync(statistics.Printer.Id, user.Id, cancellationToken);
        ActivePrint = await _historyService.GetActiveAsync(statistics.Printer.Id, user.Id, cancellationToken);
        History = await _historyService.ListAsync(statistics.Printer.Id, user.Id, cancellationToken);
        StopperNames = await _historyService.GetStopperNamesAsync(History, cancellationToken);
        HoldReason = await _historyService.GetHoldReasonAsync(statistics.Printer.Id, user.Id, cancellationToken);

        Cameras = await _cameraAccess.ListForPrinterAsync(statistics.Printer.Id, user.Id, cancellationToken);

        QueueSnapshot snapshot = await _snapshots.ReadAsync(statistics.Printer.Id, cancellationToken);
        WaitingOn = QueueWaitDescription.For(QueueRules.Decide(snapshot), snapshot.Head?.FileName);

        return Page();
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
        return ActAsync(uuid, async (userId, printer) =>
        {
            bool moved = await _queueService.MoveAsync(id, userId, position, cancellationToken);

            return moved ? ("Queue reordered.", true) : ("That print is no longer in the queue.", false);
        }, cancellationToken);
    }

    /// <summary>
    /// Cancels a queued print. Never stops a print that has already started - see
    /// <see cref="PrintQueueService.CancelAsync"/>.
    /// </summary>
    public Task<IActionResult> OnPostCancelAsync(Guid uuid, Guid id, CancellationToken cancellationToken)
    {
        return ActAsync(uuid, async (userId, printer) =>
        {
            bool cancelled = await _queueService.CancelAsync(id, userId, cancellationToken);

            return cancelled ? ("Removed from the queue.", true) : ("That print is no longer in the queue.", false);
        }, cancellationToken);
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
        return ActAsync(uuid, async (userId, printer) =>
        {
            FilamentPreset? preset = FilamentPreset.Find(printer.Model, filament);

            if (preset is null)
            {
                return ($"'{filament}' is not a filament this printer has a preset for.", false);
            }

            await _preheat.PreheatAsync(printer.Id, userId, preset, cancellationToken);

            return ($"Heating to {preset.NozzleTemperature} °C nozzle and {preset.BedTemperature} °C bed for {preset.Name}.",
                true);
        }, cancellationToken);
    }

    /// <summary>Turns both heaters off.</summary>
    public Task<IActionResult> OnPostCooldownAsync(Guid uuid, CancellationToken cancellationToken)
    {
        return ActAsync(uuid, async (userId, printer) =>
        {
            await _preheat.CooldownAsync(printer.Id, userId, cancellationToken);

            return ("Both heaters switched off.", true);
        }, cancellationToken);
    }

    /// <summary>
    /// The half both handlers share: resolve the caller and the printer, run the action, and come
    /// back to the page with something to say.
    /// </summary>
    /// <remarks>
    /// The <see cref="TeamAccessDeniedException"/> arm is what a caller holding <c>CanRead</c> alone
    /// gets if they post anyway. The buttons are not rendered for them - but a button that is not
    /// rendered is not a permission check.
    /// </remarks>
    private async Task<IActionResult> ActAsync(Guid uuid,
                                               Func<long, Printer, Task<(string message, bool success)>> action,
                                               CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return Forbid();
        }

        // Resolved rather than trusted: this is what makes a uuid the caller cannot read a 404 here
        // as much as on the GET, instead of an id going straight to the queue service.
        Printer? printer = await _printerQueryService.GetPrinterForUserAsync(uuid, user.Id, cancellationToken);

        if (printer is null)
        {
            return NotFound();
        }

        try
        {
            (StatusMessage, StatusSuccess) = await action(user.Id, printer);
        }
        catch (TeamAccessDeniedException)
        {
            return Forbid();
        }
        catch (PrinterBusyException e)
        {
            // Not an error page: the printer is doing something, which is an answer rather than a
            // fault, and the page is where the person already is.
            (StatusMessage, StatusSuccess) = (e.Message, false);
        }
        catch (PrinterRefusedException e)
        {
            // The printer's own words. Without this the page reported success for a command the
            // printer had declined, which is worse than reporting nothing.
            (StatusMessage, StatusSuccess) = (e.Message, false);
        }
        catch (Exception e) when (e is PrinterNotConnectedException or CommandAlreadyInFlightException
                                      or CommandResponseTimedOutException or CommandSendTimedOutException)
        {
            (StatusMessage, StatusSuccess) = (e.Message, false);
        }

        return RedirectToPage(new { uuid });
    }
}
