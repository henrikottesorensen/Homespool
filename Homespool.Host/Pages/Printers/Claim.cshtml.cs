using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
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
    private readonly PrusaConnectService _prusaConnectService;
    private readonly TeamService _teamService;
    private readonly UserManager<HSUser> _userManager;
    private readonly UnitOfWork _unitOfWork;
    private readonly ClaimAttemptLimiter _attemptLimiter;
    private readonly ILogger<ClaimModel> _logger;

    public ClaimModel(PrusaConnectService prusaConnectService,
                      TeamService teamService,
                      UserManager<HSUser> userManager,
                      UnitOfWork unitOfWork,
                      ClaimAttemptLimiter attemptLimiter,
                      ILogger<ClaimModel> logger)
    {
        _prusaConnectService = prusaConnectService;
        _teamService = teamService;
        _userManager = userManager;
        _unitOfWork = unitOfWork;
        _attemptLimiter = attemptLimiter;
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
        /// <summary>The registration code as typed, before normalisation.</summary>
        /// <remarks>
        /// The bound length is generous rather than exactly ten, because
        /// <see cref="ClaimCode.Normalise"/> has not run yet at validation time - someone pasting
        /// <c>ABCDE-FGHJK</c> or typing spaces is submitting a longer string than the code is. The
        /// real length check is the lookup itself.
        /// </remarks>
        [Required(ErrorMessage = "Enter the code shown on the printer's screen.")]
        [StringLength(32, ErrorMessage = "That doesn't look like a registration code.")]
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

        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            // [Authorize] should make this unreachable; fail closed rather than claim on an
            // invented id.
            return Forbid();
        }

        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        if (_attemptLimiter.RemainingLockout(user, now) is { } remaining)
        {
            // Deliberately says how long, rather than a bare refusal: the overwhelmingly likely
            // person reading this is someone who mistyped, standing at their own printer.
            ModelState.AddModelError(string.Empty,
                $"Too many unrecognised codes. Try again in {FormatWait(remaining)}. "
                + "The printer's own code is unaffected - it is still waiting.");

            return Page();
        }

        // Codes are generated in Crockford base32 uppercase (CodeGenerator) and the TemporaryCode
        // lookup has no case-insensitive collation, so a code typed off a printer's screen with
        // different casing, stray whitespace or grouping hyphens would otherwise silently read as
        // unknown. Normalise also applies Crockford's O/I/L substitutions, which is what makes a
        // character misread off a low-resolution LCD still resolve.
        string code = ClaimCode.Normalise(Input.Code);

        await using IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            Printer printer = await _prusaConnectService.ClaimPrinterAsync(
                code, Input.Name, Input.Location, Input.TeamId, user.Id);

            // Inside the transaction the claim was made in, so a rollback takes the reset with it.
            await _attemptLimiter.ResetAsync(user, cancellationToken);

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
            // The one outcome that is a guess. An already-claimed code and a forbidden team both
            // mean the code was *right*, so neither counts - otherwise a user claiming into the
            // wrong team would lock themselves out for getting the code perfectly correct.
            //
            // Recorded after the transaction has rolled back, on the limiter's own save, so the
            // rollback cannot undo the count.
            await _attemptLimiter.RecordFailedAttemptAsync(user, now, cancellationToken);

            ModelState.AddModelError(string.Empty,
                "No printer is waiting with that code. Check it against the printer's screen - codes expire, so it "
                + "may have already been replaced. Letters O, I and L are read as 0, 1 and 1, so those are safe to "
                + "get wrong.");

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

    /// <summary>
    /// Renders a backoff as something worth reading on a form - "45 seconds", "3 minutes" - rather
    /// than a raw <see cref="TimeSpan"/>.
    /// </summary>
    /// <remarks>
    /// Rounds up, so the message never tells someone to retry a moment before they may.
    /// </remarks>
    private static string FormatWait(TimeSpan remaining)
    {
        if (remaining < TimeSpan.FromMinutes(1))
        {
            int seconds = (int)Math.Ceiling(remaining.TotalSeconds);

            return seconds == 1 ? "1 second" : $"{seconds} seconds";
        }

        int minutes = (int)Math.Ceiling(remaining.TotalMinutes);

        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
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
