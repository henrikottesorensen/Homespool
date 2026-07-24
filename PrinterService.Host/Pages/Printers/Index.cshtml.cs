using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

using PrinterService.Host.Exceptions;
using PrinterService.Host.PrusaConnect;
using PrinterService.Host.PrusaConnect.Commands;
using PrinterService.Host.Services;
using PrinterService.Model;
using PrinterService.Model.Entities;

namespace PrinterService.Host.Pages.Printers;

/// <summary>
/// Lists printers the signed-in user can see, and offers "Regenerate" for any still-unbound USB-key
/// provisioning token - the only way to recover the ini snippet after leaving <c>Add</c>, since the
/// plaintext token is never stored and cannot be shown again otherwise.
/// </summary>
[Authorize]
public class IndexModel : PageModel
{
    private readonly PrinterQueryService _printerQueryService;
    private readonly PrusaConnectService _prusaConnectService;
    private readonly TeamService _teamService;
    private readonly UserManager<PSUser> _userManager;
    private readonly PrusaConnectOptions _options;
    private readonly PrinterConnectionRegistry _connectionRegistry;
    private readonly PrinterCommandService _printerCommandService;

    public IndexModel(PrinterQueryService printerQueryService,
                      PrusaConnectService prusaConnectService,
                      TeamService teamService,
                      UserManager<PSUser> userManager,
                      IOptions<PrusaConnectOptions> options,
                      PrinterConnectionRegistry connectionRegistry,
                      PrinterCommandService printerCommandService)
    {
        _printerQueryService = printerQueryService;
        _prusaConnectService = prusaConnectService;
        _teamService = teamService;
        _userManager = userManager;
        _options = options.Value;
        _connectionRegistry = connectionRegistry;
        _printerCommandService = printerCommandService;
    }

    public IReadOnlyList<PrinterRow> Printers { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    /// <summary>
    /// Whether <see cref="StatusMessage"/> reports success rather than a failure. Defaults false so
    /// every existing caller (only <see cref="OnPostRegenerateAsync"/> sets the message today, and
    /// only on failure) keeps rendering the warning styling unchanged.
    /// </summary>
    [TempData]
    public bool StatusSuccess { get; set; }

    /// <summary>The printer a regenerate just succeeded for, so the view can show its snippet once.</summary>
    public int? RegeneratedPrinterId { get; private set; }

    public string? Snippet { get; private set; }

    public record PrinterRow(Printer Printer, string TeamName, bool Enrolled, bool AwaitingUsbProvisioning, bool Connected);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadPrintersAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostRegenerateAsync(int printerId, CancellationToken cancellationToken)
    {
        PSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            // [Authorize] should make this unreachable; fail closed rather than act on an invented id.
            return Forbid();
        }

        try
        {
            string token = await _prusaConnectService.RegenerateProvisioningTokenAsync(printerId, user.Id);

            RegeneratedPrinterId = printerId;
            Snippet = ConnectIniSnippet.Build(_options, token);
        }
        catch (PrinterNotFoundException)
        {
            StatusMessage = "That printer no longer exists.";
        }
        catch (TeamAccessDeniedException)
        {
            StatusMessage = "You don't have permission to manage that printer.";
        }
        catch (ProvisioningTokenNotFoundException)
        {
            StatusMessage = "That printer was never set up with a USB key, so there is no token to reissue.";
        }

        // Not a redirect: the whole point of this handler is to show a secret exactly once, and a
        // redirect would need somewhere to carry it (TempData is the wrong place for a bearer token).
        await LoadPrintersAsync(cancellationToken);

        return Page();
    }

    public Task<IActionResult> OnPostPauseAsync(int printerId, CancellationToken cancellationToken) =>
        SendCommandAsync(printerId, new PausePrint(), cancellationToken);

    public Task<IActionResult> OnPostResumeAsync(int printerId, CancellationToken cancellationToken) =>
        SendCommandAsync(printerId, new ResumePrint(), cancellationToken);

    public Task<IActionResult> OnPostStopAsync(int printerId, CancellationToken cancellationToken) =>
        SendCommandAsync(printerId, new StopPrint(), cancellationToken);

    private async Task<IActionResult> SendCommandAsync(int printerId, ISendableCommand command, CancellationToken cancellationToken)
    {
        PSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            // [Authorize] should make this unreachable; fail closed rather than act on an invented id.
            return Forbid();
        }

        try
        {
            CommandOutcome outcome = await _printerCommandService.SendCommandAsync(printerId, command, user.Id, cancellationToken);

            (StatusMessage, StatusSuccess) = outcome.EventType switch
            {
                Events.Rejected or Events.Failed => ($"{command.WireName} rejected: {outcome.Reason}", false),
                _ => ($"{command.WireName} sent.", true),
            };
        }
        catch (PrinterNotFoundException)
        {
            (StatusMessage, StatusSuccess) = ("That printer no longer exists.", false);
        }
        catch (TeamAccessDeniedException)
        {
            (StatusMessage, StatusSuccess) = ("You don't have permission to control that printer.", false);
        }
        catch (PrinterNotConnectedException)
        {
            (StatusMessage, StatusSuccess) = ("That printer isn't connected right now.", false);
        }
        catch (CommandAlreadyInFlightException)
        {
            (StatusMessage, StatusSuccess) = ("That printer is still processing a previous command.", false);
        }
        catch (CommandTimedOutException)
        {
            (StatusMessage, StatusSuccess) = ("That printer didn't respond in time.", false);
        }

        return RedirectToPage();
    }

    private async Task LoadPrintersAsync(CancellationToken cancellationToken)
    {
        PSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            Printers = [];
            return;
        }

        IReadOnlyList<Printer> printers = await _printerQueryService.ListPrintersForUserAsync(user.Id, cancellationToken);

        if (printers.Count == 0)
        {
            Printers = [];
            return;
        }

        IReadOnlyList<TeamMember> memberships = await _teamService.GetTeamsForUserAsync(user.Id, cancellationToken);
        Dictionary<int, string> teamNames = memberships
            .Where(m => m.Team is not null)
            .ToDictionary(m => m.TeamId, m => m.Team!.Name ?? $"Team #{m.TeamId}");

        PrinterEnrollmentStatus status = await _prusaConnectService.GetEnrollmentStatusAsync(
            printers.Select(p => p.Id).ToList(), cancellationToken);

        Printers = printers
            .Select(p => new PrinterRow(
                p,
                teamNames.TryGetValue(p.TeamId, out string? name) ? name : $"Team #{p.TeamId}",
                status.Enrolled.Contains(p.Id),
                status.AwaitingUsbProvisioning.Contains(p.Id),
                _connectionRegistry.IsConnected(p.Id)))
            .ToList();
    }
}
