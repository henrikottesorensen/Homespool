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

using Homespool.Host.Exceptions;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Printers;

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
    private readonly ProvisioningBundleBuilder _bundles;
    private readonly TeamService _teamService;
    private readonly UserManager<HSUser> _userManager;
    private readonly PrusaConnectOptions _options;
    private readonly PrinterConnectionRegistry _connectionRegistry;
    private readonly PrinterCommandService _printerCommandService;

    public IndexModel(PrinterQueryService printerQueryService,
                      PrusaConnectService prusaConnectService,
                      ProvisioningBundleBuilder bundles,
                      TeamService teamService,
                      UserManager<HSUser> userManager,
                      IOptions<PrusaConnectOptions> options,
                      PrinterConnectionRegistry connectionRegistry,
                      PrinterCommandService printerCommandService)
    {
        _printerQueryService = printerQueryService;
        _prusaConnectService = prusaConnectService;
        _bundles = bundles;
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

    /// <summary>The bundle a reissue just made available, shown once and then gone.</summary>
    public BundleOffer? Offer { get; private set; }

    public record PrinterRow(Printer Printer, string TeamName, bool Enrolled, bool AwaitingUsbProvisioning, bool Connected);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadPrintersAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostRegenerateAsync(int printerId, CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            // [Authorize] should make this unreachable; fail closed rather than act on an invented id.
            return Forbid();
        }

        try
        {
            string token = await _prusaConnectService.RegenerateProvisioningTokenAsync(printerId, user.Id);

            RegeneratedPrinterId = printerId;

            IReadOnlyList<Certificates.PrinterAddressSuggestion> names = await _bundles.AvailableNamesAsync(cancellationToken);

            Offer = new BundleOffer(
                printerId,
                PrinterName: null,
                token,
                names,
                ConnectIni.BuildSnippet(_options, names.Count > 0 ? names[0].Value : _options.PrinterHost, token),
                _options.PrinterTls);
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

        // The name only reaches the offer once the list has been loaded, and it is worth the second
        // step: it is what tells two downloads in the same folder apart.
        if (Offer is not null)
        {
            Offer = Offer with
            {
                PrinterName = Printers.Where(row => row.Printer.Id == printerId).Select(row => row.Printer.Name).FirstOrDefault(),
            };
        }

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
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            // [Authorize] should make this unreachable; fail closed rather than act on an invented id.
            return Forbid();
        }

        try
        {
            CommandOutcome? outcome = await _printerCommandService.SendCommandAsync(printerId, command, user.Id, cancellationToken);

            // Null means the command was written and no answer is expected of it - success. Only the
            // three buttons on this page reach here, and all of them are answered, so this is a
            // guard rather than a live case.
            (StatusMessage, StatusSuccess) = outcome?.EventType switch
            {
                Events.Rejected or Events.Failed => ($"{command.WireName} rejected: {outcome!.Reason}", false),
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
        catch (CommandResponseTimedOutException)
        {
            (StatusMessage, StatusSuccess) = ("That printer didn't respond in time.", false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Everything above is a designed outcome PrinterCommandService can throw. This is the
            // fallback for what it can't predict - e.g. a WebSocket write racing a disconnect
            // (PrinterConnectionActor propagates a failed socket write to the caller as whatever
            // the socket layer produced, rather than a typed exception).
            // Without this, that unlikely-but-real race surfaces as an unhandled 500 instead of a
            // message. Excluded when the request itself was cancelled - nothing will render anyway.
            (StatusMessage, StatusSuccess) = ("Something went wrong sending the command.", false);
        }

        return RedirectToPage();
    }

    private async Task LoadPrintersAsync(CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

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

        PrinterEnrolmentStatus status = await _prusaConnectService.GetEnrolmentStatusAsync(
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
