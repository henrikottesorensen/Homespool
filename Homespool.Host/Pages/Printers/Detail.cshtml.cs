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
    private readonly TeamService _teamService;
    private readonly PrinterConnectionRegistry _connectionRegistry;
    private readonly UserManager<HSUser> _userManager;

    public DetailModel(PrinterQueryService printerQueryService,
                       PrintQueueService queueService,
                       TeamService teamService,
                       PrinterConnectionRegistry connectionRegistry,
                       UserManager<HSUser> userManager)
    {
        _printerQueryService = printerQueryService;
        _queueService = queueService;
        _teamService = teamService;
        _connectionRegistry = connectionRegistry;
        _userManager = userManager;
    }

    public PrinterStatistics Statistics { get; private set; } = null!;

    public bool Connected { get; private set; }

    /// <summary>What this printer will print, in order. Empty until somebody queues something.</summary>
    public IReadOnlyList<QueuedPrint> Queue { get; private set; } = [];

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
