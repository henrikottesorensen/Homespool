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

using Homespool.Host.Accounts;
using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Host.Localisation;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect;
using Homespool.Host.Queue;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Printers;

/// <summary>
/// Every printer the signed-in user can see, one card each: the drawing and plaque the front page's
/// tiles carry, plus the two things the tiles have no room for - making one the default, and
/// reissuing a USB-key token. Reissuing here is the only way to recover the ini snippet after
/// leaving <c>Add</c>, since the plaintext token is never stored and cannot be shown again otherwise.
/// </summary>
/// <remarks>
/// <b>Pause, resume and stop are deliberately not here.</b> They are on the printer's own page,
/// beside the status they act on and the printer's answer to them. A rack of stop buttons, one per
/// printer, is a rack of ways to stop the wrong one.
/// </remarks>
[Authorize]
public class IndexModel : PageModel
{
    private readonly PrinterQueryService _printerQueryService;
    private readonly PrusaConnectService _prusaConnectService;
    private readonly DefaultPrinterService _defaults;
    private readonly ProvisioningBundleBuilder _bundles;
    private readonly TeamService _teamService;
    private readonly PrintQueueService _queue;
    private readonly UserManager<HSUser> _userManager;
    private readonly PrusaConnectOptions _options;
    private readonly PrinterConnectionRegistry _connectionRegistry;
    private readonly PrinterStatusText _statusText;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public IndexModel(PrinterQueryService printerQueryService,
                      PrusaConnectService prusaConnectService,
                      DefaultPrinterService defaults,
                      ProvisioningBundleBuilder bundles,
                      TeamService teamService,
                      PrintQueueService queue,
                      UserManager<HSUser> userManager,
                      IOptionsSnapshot<PrusaConnectOptions> options,
                      PrinterConnectionRegistry connectionRegistry,
                      PrinterStatusText statusText,
                      IStringLocalizer<SharedResource> localiser)
    {
        _printerQueryService = printerQueryService;
        _prusaConnectService = prusaConnectService;
        _defaults = defaults;
        _bundles = bundles;
        _teamService = teamService;
        _queue = queue;
        _userManager = userManager;
        _options = options.Value;
        _connectionRegistry = connectionRegistry;
        _statusText = statusText;
        _localiser = localiser;
    }

    public IReadOnlyList<PrinterRow> Printers { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    /// <summary>
    /// Whether <see cref="StatusMessage"/> reports success rather than a failure. Defaults false, so
    /// a message set without saying renders as a warning.
    /// </summary>
    [TempData]
    public bool StatusSuccess { get; set; }

    /// <summary>The printer a regenerate just succeeded for, so the view can show its snippet once.</summary>
    public int? RegeneratedPrinterId { get; private set; }

    /// <summary>
    /// The reader's default printer, so exactly one row can say so.
    /// </summary>
    /// <remarks>
    /// Taken as stored rather than resolved against permissions: this list is already only printers
    /// the reader may see, so an id naming anything else simply matches no row.
    /// </remarks>
    public int? DefaultPrinterId { get; private set; }

    /// <summary>The bundle a reissue just made available, shown once and then gone.</summary>
    public BundleOffer? Offer { get; private set; }

    /// <summary>One card of the listing.</summary>
    /// <remarks>
    /// <para>
    /// <b><paramref name="LiveStatus"/> is the printer's own, and null until it has ever reported.</b>
    /// Deliberately not <c>Printer.Status</c>, which is written once as <c>Unknown</c> when the row is
    /// created and never updated again - see <see cref="PrinterQueryService"/>.
    /// </para>
    /// <para>
    /// <b>The four readings after it follow the front page's rule for a disconnected printer</b>:
    /// <see cref="PrinterLiveState"/> persists, so every field on it outlives the connection, and
    /// <paramref name="Progress"/> and <paramref name="TimeRemaining"/> are dropped when it is gone
    /// because they describe a print nobody can watch. <paramref name="Material"/> stays: what is
    /// loaded does not change while the power is off. See <see cref="PrinterShortcut"/>.
    /// </para>
    /// </remarks>
    public record PrinterRow(
        Printer Printer,
        string TeamName,
        bool Enrolled,
        bool AwaitingUsbProvisioning,
        bool ExpiredUsbProvisioning,
        bool Connected,
        PrinterStatus? LiveStatus,
        PrinterFormFactor FormFactor,
        int? Progress,
        int? TimeRemaining,
        string? Material,
        int QueuedCount);

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

    /// <summary>
    /// The rack on its own, for the poll.
    /// </summary>
    /// <remarks>
    /// <b>It loads exactly what the page load loads</b>, because the partial may only render state
    /// its own handler provides - the rule the printer page's queue fragment was extracted under, and
    /// broke. The bundle offer and the status message are outside the partial on purpose: a poll has
    /// no offer to show and no message to repeat, and a fragment that carried either would be
    /// blanking them every ten seconds.
    /// </remarks>
    public async Task<IActionResult> OnGetRackAsync(CancellationToken cancellationToken)
    {
        await LoadPrintersAsync(cancellationToken);

        return Partial("_PrinterRack", this);
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
            string token = await _prusaConnectService.RegenerateProvisioningTokenAsync(printerId, CallerResolver.For(user, User));

            RegeneratedPrinterId = printerId;

            IReadOnlyList<Certificates.PrinterAddressSuggestion> names = await _bundles.AvailableNamesAsync(cancellationToken);

            Offer = new BundleOffer(
                printerId,
                token,
                names,
                ConnectIni.BuildSnippet(PrinterEndpoint.Default(_options), names.Count > 0 ? names[0].Value : _options.PrinterHost, token),
                _options.PrinterTls,
                _options.LegacyPrinterPort);
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

        // The firmware only reaches the offer once the list has been loaded, and it is worth the
        // second step - it is also why this is a reissue rather than a first provisioning: this
        // printer has connected before, so it has told us what it runs, and the plaintext listener
        // can be argued against specifically rather than in general.
        if (Offer is not null)
        {
            Offer = Offer with
            {
                KnownFirmware = Printers.Where(row => row.Printer.Id == printerId).Select(row => row.Printer.Firmware).FirstOrDefault(),
            };
        }

        return Page();
    }

    /// <summary>
    /// Makes one row's printer the reader's default.
    /// </summary>
    /// <remarks>
    /// <b>Only ever sets, never clears</b> - the button renders on the rows that are not the default,
    /// so the listing's whole vocabulary is "make it this one instead". Turning the idea off entirely
    /// is on the printer's own page, where there is room to say what it means.
    /// </remarks>
    public async Task<IActionResult> OnPostDefaultAsync(int printerId, CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return Forbid();
        }

        Caller caller = CallerResolver.For(user, User);

        (StatusMessage, StatusSuccess) = await _defaults.SetAsync(user, caller, printerId, cancellationToken) ?
            (_localiser["Printers_DefaultSaved"].Value, true) :
            (_localiser["Printers_DefaultNotSaved"].Value, false);

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

        DefaultPrinterId = user.DefaultPrinterId;

        // With state, because the Status column reports what a connected printer is doing rather than
        // only that it is enrolled. Same query shape, one join.
        IReadOnlyList<PrinterWithState> printers =
            await _printerQueryService.ListPrintersWithStateForUserAsync(CallerResolver.For(user, User), cancellationToken);

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

        List<int> ids = printers.Select(row => row.Printer.Id).ToList();

        PrinterEnrolmentStatus status = await _prusaConnectService.GetEnrolmentStatusAsync(ids, cancellationToken);

        // One grouped count for the whole rack rather than a queue read per printer, the same call
        // the front page makes for its tiles.
        IReadOnlyDictionary<int, int> queued = await _queue.CountByPrinterAsync(ids, cancellationToken);

        Printers = printers.Select(row => RowFor(row, teamNames, status, queued)).ToList();
    }

    private PrinterRow RowFor(PrinterWithState row,
                              IReadOnlyDictionary<int, string> teamNames,
                              PrinterEnrolmentStatus status,
                              IReadOnlyDictionary<int, int> queued)
    {
        bool connected = _connectionRegistry.IsConnected(row.Printer.Id);

        return new PrinterRow(
            row.Printer,
            teamNames.TryGetValue(row.Printer.TeamId, out string? name) ?
                name :
                _localiser["Common_TeamNumbered", row.Printer.TeamId].Value,
            status.Enrolled.Contains(row.Printer.Id),
            status.AwaitingUsbProvisioning.Contains(row.Printer.Id),
            status.ExpiredUsbProvisioning.Contains(row.Printer.Id),
            connected,
            row.LiveState?.Status,
            PrinterFormFactors.For(row.LiveState),
            connected ? row.LiveState?.Progress : null,
            connected ? row.LiveState?.TimeRemaining : null,
            row.LiveState?.Material,
            queued.TryGetValue(row.Printer.Id, out int waiting) ? waiting : 0);
    }
}
