using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

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
    private readonly PrintHistoryService _historyService;
    private readonly QueueSnapshotReader _snapshots;
    private readonly TeamService _teamService;
    private readonly PrinterConnectionRegistry _connectionRegistry;
    private readonly UserManager<HSUser> _userManager;

    public DetailModel(PrinterQueryService printerQueryService,
                       PrintQueueService queueService,
                       PrintHistoryService historyService,
                       QueueSnapshotReader snapshots,
                       TeamService teamService,
                       PrinterConnectionRegistry connectionRegistry,
                       UserManager<HSUser> userManager)
    {
        _printerQueryService = printerQueryService;
        _queueService = queueService;
        _historyService = historyService;
        _snapshots = snapshots;
        _teamService = teamService;
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

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public bool StatusSuccess { get; set; }

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

        PrinterStatistics? statistics = await _printerQueryService.GetPrinterStatisticsForUserAsync(uuid, user.Id, cancellationToken);

        if (statistics is null)
        {
            return NotFound();
        }

        Statistics = statistics;
        Connected = _connectionRegistry.IsConnected(statistics.Printer.Id);

        TeamMember? membership = await _teamService.GetMemberAsync(statistics.Printer.TeamId, user.Id, cancellationToken);
        CanUse = membership?.CanUse ?? false;

        Queue = await _queueService.ListAsync(statistics.Printer.Id, user.Id, cancellationToken);
        ActivePrint = await _historyService.GetActiveAsync(statistics.Printer.Id, user.Id, cancellationToken);
        History = await _historyService.ListAsync(statistics.Printer.Id, user.Id, cancellationToken);
        StopperNames = await _historyService.GetStopperNamesAsync(History, cancellationToken);
        HoldReason = await _historyService.GetHoldReasonAsync(statistics.Printer.Id, user.Id, cancellationToken);

        QueueSnapshot snapshot = await _snapshots.ReadAsync(statistics.Printer.Id, cancellationToken);
        WaitingOn = QueueWaitDescription.For(QueueRules.Decide(snapshot), snapshot.Head?.FileName);

        return Page();
    }

    /// <summary>Moves a queued print to a new position.</summary>
    /// <remarks>
    /// Takes the target index rather than a direction, because that is what the service takes and
    /// where the clamping lives - the page only does the arithmetic its two buttons imply.
    /// </remarks>
    public Task<IActionResult> OnPostMoveAsync(Guid uuid, long id, int position,
        CancellationToken cancellationToken)
    {
        return ActAsync(uuid, async userId =>
        {
            bool moved = await _queueService.MoveAsync(id, userId, position, cancellationToken);

            return moved
                ? ("Queue reordered.", true)
                : ("That print is no longer in the queue.", false);
        }, cancellationToken);
    }

    /// <summary>
    /// Cancels a queued print. Never stops a print that has already started - see
    /// <see cref="PrintQueueService.CancelAsync"/>.
    /// </summary>
    public Task<IActionResult> OnPostCancelAsync(Guid uuid, long id, CancellationToken cancellationToken)
    {
        return ActAsync(uuid, async userId =>
        {
            bool cancelled = await _queueService.CancelAsync(id, userId, cancellationToken);

            return cancelled
                ? ("Removed from the queue.", true)
                : ("That print is no longer in the queue.", false);
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
    private async Task<IActionResult> ActAsync(Guid uuid, Func<long, Task<(string message, bool success)>> action,
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
            (StatusMessage, StatusSuccess) = await action(user.Id);
        }
        catch (TeamAccessDeniedException)
        {
            return Forbid();
        }

        return RedirectToPage(new { uuid });
    }
}
