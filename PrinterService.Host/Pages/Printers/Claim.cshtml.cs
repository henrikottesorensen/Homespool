using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

using PrinterService.Host.Exceptions;
using PrinterService.Host.PrusaConnect;
using PrinterService.Host.Services;
using PrinterService.Model.Entities;

namespace PrinterService.Host.Pages.Printers;

/// <summary>
/// The registration-code enrollment channel's web UI: a signed-in user redeems the code a printer
/// is displaying on its own screen, linking it to their account. Unlike
/// <see cref="AddModel"/>'s USB-key path, nothing new is generated here for the user to copy - the
/// printer already has its credential-in-waiting - so a successful claim redirects to the printer
/// list rather than showing a one-time secret. The printer itself still has to poll
/// <c>GET /p/register</c> to redeem its own token; immediately after claiming, it shows on the list
/// as "Awaiting connection".
/// </summary>
[Authorize]
public class ClaimModel : PageModel
{
    private readonly PrusaConnectService _prusaConnectService;
    private readonly TeamService _teamService;
    private readonly UserManager<PSUser> _userManager;
    private readonly UnitOfWork _unitOfWork;
    private readonly ILogger<ClaimModel> _logger;

    public ClaimModel(PrusaConnectService prusaConnectService,
                      TeamService teamService,
                      UserManager<PSUser> userManager,
                      UnitOfWork unitOfWork,
                      ILogger<ClaimModel> logger)
    {
        _prusaConnectService = prusaConnectService;
        _teamService = teamService;
        _userManager = userManager;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<SelectListItem> TeamOptions { get; private set; } = [];

    /// <summary>
    /// Set on success, before redirecting to <c>Index</c>. Property name matches
    /// <see cref="IndexModel.StatusMessage"/> exactly - that is what makes a <c>[TempData]</c> value
    /// set here readable by that page after the redirect.
    /// </summary>
    [TempData]
    public string? StatusMessage { get; set; }

    /// <summary>Matches <see cref="IndexModel.StatusSuccess"/> by name, same reasoning as above.</summary>
    [TempData]
    public bool StatusSuccess { get; set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Enter the code shown on the printer's screen.")]
        [StringLength(64, ErrorMessage = "That doesn't look like a registration code.")]
        [Display(Name = "Registration code")]
        public string Code { get; set; } = string.Empty;

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

        if (!ModelState.IsValid)
        {
            return Page();
        }

        PSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            // [Authorize] should make this unreachable; fail closed rather than claim on an
            // invented id.
            return Forbid();
        }

        // Codes are generated in uppercase (CodeGenerator: SimpleBase.Base36.UpperCase) and the
        // TemporaryCode lookup has no case-insensitive collation, so a code typed off a printer's
        // screen with different casing or stray whitespace would otherwise silently read as unknown.
        string code = Input.Code.Trim().ToUpperInvariant();

        await using IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            Printer printer = await _prusaConnectService.ClaimPrinterAsync(
                code, Input.Name, Input.Location, Input.TeamId, user.Id);

            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation("Printer {PrinterUuid} claimed via registration code by user {UserId}.", printer.Uuid, user.Id);

            // Set via the [TempData]-attributed properties below, not PageModel.TempData directly -
            // matching Admin/Invites/IndexModel.OnPostRevokeAsync's pattern. The property names match
            // IndexModel's own [TempData] properties (the default TempData key is the property name),
            // which is what lets the message survive the redirect to a different page.
            StatusMessage = "Printer claimed. It will show as connected once it completes its next check-in.";
            StatusSuccess = true;

            return RedirectToPage("Index");
        }
        catch (PrinterNotFoundException)
        {
            ModelState.AddModelError(string.Empty,
                "No printer is waiting with that code. Check it against the printer's screen - codes expire, so it may have already been replaced.");

            return Page();
        }
        catch (RegistrationAlreadyClaimedException)
        {
            ModelState.AddModelError(string.Empty,
                "This code has already been claimed. If that wasn't you, make sure you copied the current code from the printer's screen.");

            return Page();
        }
        catch (TeamAccessDeniedException)
        {
            ModelState.AddModelError(string.Empty, "You don't have permission to claim a printer into that team.");

            return Page();
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to claim printer for user {UserId}; rolling back.", user.Id);

            ModelState.AddModelError(string.Empty, "Something went wrong claiming the printer. Please try again.");

            return Page();
        }
    }

    private async Task LoadTeamOptionsAsync(CancellationToken cancellationToken)
    {
        PSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            TeamOptions = [];
            return;
        }

        IReadOnlyList<TeamMember> memberships = await _teamService.GetTeamsForUserAsync(user.Id, cancellationToken);

        TeamOptions = TeamOptionSelectListBuilder.BuildManageableOptions(memberships);
    }
}
