using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.Exceptions;
using Homespool.Host.PrusaConnect;
using Homespool.Host.Services;
using Homespool.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Pages.Printers;

/// <summary>
/// USB-key provisioning: mints a printer and a pre-provisioned token up front, then shows the
/// <c>[service::connect]</c> ini snippet once for the operator to copy onto the printer's USB stick.
/// The printer never touches <c>/p/register</c> for this path - it enrols itself on first contact
/// with the token, in <see cref="Authentication.PrusaConnectPrinterAuthenticationHandler"/>.
/// </summary>
[Authorize]
public class AddModel : PageModel
{
    private readonly PrusaConnectService _prusaConnectService;
    private readonly ProvisioningBundleBuilder _bundles;
    private readonly TeamService _teamService;
    private readonly UserManager<HSUser> _userManager;
    private readonly UnitOfWork _unitOfWork;
    private readonly PrusaConnectOptions _options;
    private readonly ILogger<AddModel> _logger;

    public AddModel(PrusaConnectService prusaConnectService,
                    ProvisioningBundleBuilder bundles,
                    TeamService teamService,
                    UserManager<HSUser> userManager,
                    UnitOfWork unitOfWork,
                    IOptions<PrusaConnectOptions> options,
                    ILogger<AddModel> logger)
    {
        _prusaConnectService = prusaConnectService;
        _bundles = bundles;
        _teamService = teamService;
        _userManager = userManager;
        _unitOfWork = unitOfWork;
        _options = options.Value;
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<SelectListItem> TeamOptions { get; private set; } = [];

    /// <summary>
    /// Whether the server's own connect address (<c>PrusaConnect:PrinterHost</c>) is set. Without it
    /// the snippet would carry an empty <c>hostname</c>, so provisioning is blocked - server-side, not
    /// just by disabling the form - until an operator configures it.
    /// </summary>
    public bool PrinterAddressConfigured => _options.IsPrinterAddressConfigured;

    /// <summary>
    /// Set after a successful provision, so the view can offer the one-time bundle. Null before, and
    /// gone the moment the page is left - the token it carries exists nowhere else.
    /// </summary>
    public BundleOffer? Offer { get; private set; }

    public class InputModel
    {
        [StringLength(200)]
        [Display(Name = "Name")]
        public string? Name { get; set; }

        [StringLength(200)]
        [Display(Name = "Location")]
        public string? Location { get; set; }

        [Display(Name = "Team")]
        public int? TeamId { get; set; }
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadTeamOptionsAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await LoadTeamOptionsAsync(cancellationToken);

        if (!PrinterAddressConfigured)
        {
            // Belt and braces: the view disables the submit button for the same reason, but a raw
            // POST must be refused server-side too, or a bypassed form would hand out a snippet with
            // an empty hostname.
            ModelState.AddModelError(string.Empty,
                "The server's public address is not configured yet (PrusaConnect:PrinterHost). Ask your administrator to set it before provisioning printers this way.");

            return Page();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            // [Authorize] should make this unreachable; fail closed rather than provision on an
            // invented id.
            return Forbid();
        }

        await using IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            (Printer printer, string token) = await _prusaConnectService.ProvisionPrinterAsync(
                Input.Name, Input.Location, Input.TeamId, user.Id);

            await transaction.CommitAsync(cancellationToken);

            Offer = await BuildOfferAsync(printer.Id, Input.Name, token, cancellationToken);

            _logger.LogInformation("Printer {PrinterUuid} provisioned via USB-key by user {UserId}.", printer.Uuid, user.Id);

            // Reset the form for the next printer, but keep the snippet shown - same pattern as
            // Admin/Invites/Create's AcceptLink.
            ModelState.Clear();
            Input = new InputModel();

            return Page();
        }
        catch (TeamAccessDeniedException)
        {
            ModelState.AddModelError(string.Empty, "You don't have permission to add a printer to that team.");

            return Page();
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to provision printer for user {UserId}; rolling back.", user.Id);

            ModelState.AddModelError(string.Empty, "Something went wrong provisioning the printer. Please try again.");

            return Page();
        }
    }

    /// <summary>
    /// Wraps a freshly issued token in everything the download partial needs.
    /// </summary>
    /// <remarks>
    /// The names come from the certificate rather than from this machine's current addresses: the leaf
    /// is issued once and frozen, so the two can differ, and a bundle written for an address the
    /// certificate does not carry fails at the printer with nothing but "TLS error" to go on.
    /// </remarks>
    private async Task<BundleOffer> BuildOfferAsync(int printerId,
                                                    string? printerName,
                                                    string token,
                                                    CancellationToken cancellationToken)
    {
        IReadOnlyList<string> names = await _bundles.AvailableNamesAsync(cancellationToken);

        return new BundleOffer(
            printerId,
            printerName,
            token,
            names,
            ConnectIni.BuildSnippet(_options, names.Count > 0 ? names[0] : _options.PrinterHost, token),
            _options.PrinterTls);
    }

    private async Task LoadTeamOptionsAsync(CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            TeamOptions = [];
            return;
        }

        IReadOnlyList<TeamMember> memberships = await _teamService.GetTeamsForUserAsync(user.Id, cancellationToken);

        TeamOptions = TeamOptionSelectListBuilder.BuildManageableOptions(memberships);
    }
}
