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
using Microsoft.Extensions.Options;

using Homespool.Host.Exceptions;
using Homespool.Host.Localisation;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect;
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
    private readonly PrintStopService _printStopService;
    private readonly PrinterStatusText _statusText;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public IndexModel(PrinterQueryService printerQueryService,
                      PrusaConnectService prusaConnectService,
                      ProvisioningBundleBuilder bundles,
                      TeamService teamService,
                      UserManager<HSUser> userManager,
                      IOptions<PrusaConnectOptions> options,
                      PrinterConnectionRegistry connectionRegistry,
                      PrinterCommandService printerCommandService,
                      PrintStopService printStopService,
                      PrinterStatusText statusText,
                      IStringLocalizer<SharedResource> localiser)
    {
        _printerQueryService = printerQueryService;
        _prusaConnectService = prusaConnectService;
        _bundles = bundles;
        _teamService = teamService;
        _userManager = userManager;
        _options = options.Value;
        _connectionRegistry = connectionRegistry;
        _printerCommandService = printerCommandService;
        _printStopService = printStopService;
        _statusText = statusText;
        _localiser = localiser;
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

    /// <summary>One row of the listing.</summary>
    /// <remarks>
    /// <b><paramref name="LiveStatus"/> is the printer's own, and null until it has ever reported.</b>
    /// Deliberately not <c>Printer.Status</c>, which is written once as <c>Unknown</c> when the row is
    /// created and never updated again - see <see cref="PrinterQueryService"/>.
    /// </remarks>
    public record PrinterRow(
        Printer Printer,
        string TeamName,
        bool Enrolled,
        bool AwaitingUsbProvisioning,
        bool Connected,
        PrinterStatus? LiveStatus);

    /// <summary>
    /// The badge colour for a status - semantic, so what needs a person reads at a glance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three tiers and a default: <b>danger</b> for the two that need somebody now,
    /// <b>success</b> for a printer doing what it was asked, and <b>secondary</b> for every resting
    /// state, so a listing of idle printers stays quiet instead of glowing green.
    /// </para>
    /// <para>
    /// <b>Not localised, and worth noticing beside <see cref="StatusText"/>.</b> This returns a CSS
    /// class name — a value Bootstrap parses, not one a person reads — so translating it would
    /// produce a class that styles nothing. Same enum, same row of the page, opposite sides of the
    /// machine-text boundary.
    /// </para>
    /// </remarks>
    public static string StatusBadgeClass(PrinterStatus? status)
    {
        return status switch
        {
            PrinterStatus.Error or PrinterStatus.Attention => "text-bg-danger",
            PrinterStatus.Paused => "text-bg-warning",
            PrinterStatus.Printing or PrinterStatus.Ready => "text-bg-success",
            _ => "text-bg-secondary",
        };
    }

    /// <summary>
    /// What a connected printer's status says, in a person's words rather than the enum's — and in
    /// their language. See <see cref="Localisation.PrinterStatusText"/>.
    /// </summary>
    /// <remarks>
    /// <b>No longer static, and that is the change.</b> The words used to be written here, ending in
    /// <c>status.ToString()</c> so most states reached the page as a C# identifier that happened to
    /// read like English. There was nothing to translate because nobody had written the words down.
    /// </remarks>
    public string StatusText(PrinterStatus? status)
    {
        return _statusText.For(status);
    }

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
            StatusMessage = _localiser["Printers_NotFound"];
        }
        catch (TeamAccessDeniedException)
        {
            StatusMessage = _localiser["Printers_NotYours"];
        }
        catch (ProvisioningTokenNotFoundException)
        {
            StatusMessage = _localiser["Printers_NoUsbToken"];
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

    public Task<IActionResult> OnPostPauseAsync(int printerId, CancellationToken cancellationToken)
    {
        return SendCommandAsync(printerId, new PausePrint(), cancellationToken);
    }

    public Task<IActionResult> OnPostResumeAsync(int printerId, CancellationToken cancellationToken)
    {
        return SendCommandAsync(printerId, new ResumePrint(), cancellationToken);
    }

    /// <summary>Stops whatever this printer is running.</summary>
    /// <remarks>
    /// Through <see cref="PrintStopService"/> rather than straight to
    /// <see cref="PrinterCommandService"/>, unlike the two buttons above it: a stop is the one whose
    /// cause the printer cannot report afterwards, so who pressed it is noted as it is sent.
    /// </remarks>
    public Task<IActionResult> OnPostStopAsync(int printerId, CancellationToken cancellationToken)
    {
        return SendCommandAsync(printerId, new StopPrint(), cancellationToken, _printStopService.StopAsync);
    }

    /// <summary>
    /// Sends a command on behalf of the signed-in user and reports how it went in
    /// <see cref="StatusMessage"/>.
    /// </summary>
    /// <remarks>
    /// <b><c>send</c> is how it goes out</b>, for the one button needing more than
    /// <see cref="PrinterCommandService"/> alone. Null is the ordinary path. A replacement throws the
    /// same exceptions and returns the same <see cref="CommandOutcome"/>, so the reporting below is
    /// unchanged either way.
    /// </remarks>
    private async Task<IActionResult> SendCommandAsync(int printerId,
                                                       IPrinterIntent command,
                                                       CancellationToken cancellationToken,
                                                       Func<int, long, CancellationToken, Task<CommandOutcome?>>? send = null)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            // [Authorize] should make this unreachable; fail closed rather than act on an invented id.
            return Forbid();
        }

        try
        {
            CommandOutcome? outcome;

            if (send is null)
            {
                outcome = await _printerCommandService.SendCommandAsync(printerId, command, user.Id, cancellationToken);
            }
            else
            {
                outcome = await send(printerId, user.Id, cancellationToken);
            }

            // Null means the command was written and no answer is expected of it - success. Only the
            // three buttons on this page reach here, and all of them are answered, so this is a
            // guard rather than a live case.
            (StatusMessage, StatusSuccess) = outcome?.EventType switch
            {
                PrinterEventType.Rejected or PrinterEventType.Failed =>
                    (_localiser["Printers_CommandRejected", command.Name, outcome!.Reason ?? string.Empty], false),
                _ => (_localiser["Printers_CommandSent", command.Name], true),
            };
        }
        catch (PrinterNotFoundException)
        {
            (StatusMessage, StatusSuccess) = (_localiser["Printers_NotFound"], false);
        }
        catch (TeamAccessDeniedException)
        {
            (StatusMessage, StatusSuccess) = (_localiser["Printers_NoControlPermission"], false);
        }
        catch (PrinterNotConnectedException)
        {
            (StatusMessage, StatusSuccess) = (_localiser["Printers_NotConnectedNow"], false);
        }
        catch (CommandAlreadyInFlightException)
        {
            (StatusMessage, StatusSuccess) = (_localiser["Printers_StillBusy"], false);
        }
        catch (CommandResponseTimedOutException)
        {
            (StatusMessage, StatusSuccess) = (_localiser["Printers_NoResponse"], false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Everything above is a designed outcome PrinterCommandService can throw. This is the
            // fallback for what it can't predict - e.g. a WebSocket write racing a disconnect
            // (PrinterConnectionActor propagates a failed socket write to the caller as whatever
            // the socket layer produced, rather than a typed exception).
            // Without this, that unlikely-but-real race surfaces as an unhandled 500 instead of a
            // message. Excluded when the request itself was cancelled - nothing will render anyway.
            (StatusMessage, StatusSuccess) = (_localiser["Printers_CommandFailed"], false);
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

        // With state, because the Status column reports what a connected printer is doing rather than
        // only that it is enrolled. Same query shape, one join.
        IReadOnlyList<PrinterWithState> printers =
            await _printerQueryService.ListPrintersWithStateForUserAsync(user.Id, cancellationToken);

        if (printers.Count == 0)
        {
            Printers = [];
            return;
        }

        IReadOnlyList<TeamMember> memberships = await _teamService.GetTeamsForUserAsync(user.Id, cancellationToken);
        Dictionary<int, string> teamNames = memberships
                                            .Where(m => m.Team is not null)
                                            .ToDictionary(
                                                m => m.TeamId,
                                                m => m.Team!.Name ?? _localiser["Common_TeamNumbered", m.TeamId].Value);

        PrinterEnrolmentStatus status = await _prusaConnectService.GetEnrolmentStatusAsync(
            printers.Select(row => row.Printer.Id).ToList(), cancellationToken);

        Printers = printers
                   .Select(row => new PrinterRow(
                               row.Printer,
                               teamNames.TryGetValue(row.Printer.TeamId, out string? name) ?
                                   name :
                                   _localiser["Common_TeamNumbered", row.Printer.TeamId].Value,
                               status.Enrolled.Contains(row.Printer.Id),
                               status.AwaitingUsbProvisioning.Contains(row.Printer.Id),
                               _connectionRegistry.IsConnected(row.Printer.Id),
                               row.LiveState?.Status))
                   .ToList();
    }
}
