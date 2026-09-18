using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

using Homespool.Host.Accounts;
using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Host.Localisation;
using Homespool.Host.PrusaConnect;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Printers;

/// <summary>
/// The registration-code enrolment channel's web UI: a signed-in user redeems the code a printer
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
    private readonly RegistrationCodeClaim _registrationCodeClaim;
    private readonly TeamService _teamService;
    private readonly UserManager<HSUser> _userManager;
    private readonly ILogger<ClaimModel> _logger;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public ClaimModel(RegistrationCodeClaim registrationCodeClaim,
                      TeamService teamService,
                      UserManager<HSUser> userManager,
                      ILogger<ClaimModel> logger,
                      IStringLocalizer<SharedResource> localiser)
    {
        _registrationCodeClaim = registrationCodeClaim;
        _teamService = teamService;
        _userManager = userManager;
        _logger = logger;
        _localiser = localiser;
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
        [StringLength(Printer.NameMaxLength)]
        [Display(Name = "Common_Name")]
        public string? Name { get; set; }

        [StringLength(Printer.LocationMaxLength)]
        [Display(Name = "Printers_Location")]
        public string? Location { get; set; }

        /// <summary>The registration code as typed, before normalisation.</summary>
        /// <remarks>
        /// The bound length is generous rather than exactly ten, because
        /// <see cref="ClaimCode.Normalise"/> has not run yet at validation time - someone pasting
        /// <c>ABCDE-FGHJK</c> or typing spaces is submitting a longer string than the code is. The
        /// real length check is the lookup itself.
        /// </remarks>
        [Required(ErrorMessage = "Validation_ClaimCodeRequired")]
        [StringLength(32, ErrorMessage = "Validation_ClaimCodeShape")]
        [Display(Name = "Printers_RegistrationCode")]
        public string Code { get; set; } = string.Empty;

        [Display(Name = "Common_Team")]
        public Guid? TeamUuid { get; set; }
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

        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            // [Authorize] should make this unreachable; fail closed rather than claim on an
            // invented id.
            return Forbid();
        }

        try
        {
            Printer printer = await _registrationCodeClaim.ClaimAsync(
                user.Id, Input.Code, Input.Name, Input.Location, Input.TeamUuid, CallerResolver.For(user, User), cancellationToken);

            _logger.LogInformation("Printer {PrinterUuid} claimed via registration code by user {UserId}.", printer.Uuid, user.Id);

            // Set via the [TempData]-attributed properties below, not PageModel.TempData directly -
            // matching Admin/Invites/IndexModel.OnPostRevokeAsync's pattern. The property names match
            // IndexModel's own [TempData] properties (the default TempData key is the property name),
            // which is what lets the message survive the redirect to a different page.
            StatusMessage = _localiser["Printers_Claimed"];
            StatusSuccess = true;

            return RedirectToPage("Index");
        }
        catch (ClaimLockedOutException e)
        {
            // Deliberately says how long, rather than a bare refusal: the overwhelmingly likely
            // person reading this is someone who mistyped, standing at their own printer.
            ModelState.AddModelError(string.Empty, _localiser["Printers_ClaimLockedOut", BackoffWait.Format(_localiser, e.RetryAfter)]);

            return Page();
        }
        catch (PrinterNotFoundException)
        {
            ModelState.AddModelError(string.Empty, _localiser["Printers_ClaimNoSuchCode"]);

            return Page();
        }
        catch (RegistrationAlreadyClaimedException)
        {
            ModelState.AddModelError(string.Empty, _localiser["Printers_ClaimAlreadyClaimed"]);

            return Page();
        }
        catch (TeamAccessDeniedException)
        {
            ModelState.AddModelError(string.Empty, _localiser["Printers_ClaimNoTeamPermission"]);

            return Page();
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to claim printer for user {UserId}; rolling back.", user.Id);

            ModelState.AddModelError(string.Empty, _localiser["Printers_ClaimFailed"]);

            return Page();
        }
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

        TeamOptions = TeamOptionSelectListBuilder.BuildManageableOptions(memberships, _localiser);
    }
}
