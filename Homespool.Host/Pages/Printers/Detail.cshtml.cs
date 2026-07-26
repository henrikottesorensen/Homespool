using System;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.PrusaConnect;
using Homespool.Host.Services;
using Homespool.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Homespool.Host.Pages.Printers;

/// <summary>
/// One printer's live status plus recent telemetry/event history - the first reader of
/// <see cref="PrinterLiveState"/>/<see cref="Model.Entities.TelemetrySample"/>/
/// <see cref="Model.Entities.PrinterEvent"/> anywhere in the app.
/// </summary>
[Authorize]
public class DetailModel : PageModel
{
    private readonly PrinterQueryService _printerQueryService;
    private readonly PrinterConnectionRegistry _connectionRegistry;
    private readonly UserManager<HSUser> _userManager;

    public DetailModel(PrinterQueryService printerQueryService, PrinterConnectionRegistry connectionRegistry, UserManager<HSUser> userManager)
    {
        _printerQueryService = printerQueryService;
        _connectionRegistry = connectionRegistry;
        _userManager = userManager;
    }

    public PrinterStatistics Statistics { get; private set; } = null!;

    public bool Connected { get; private set; }

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

        return Page();
    }
}
