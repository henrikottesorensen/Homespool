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
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Host.Localisation;
using Homespool.Host.PrusaConnect;
using Homespool.Host.Services;
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
    private readonly PrusaConnectService _prusaConnectService;
    private readonly TeamService _teamService;
    private readonly UserManager<HSUser> _userManager;
    private readonly UnitOfWork _unitOfWork;
    private readonly ClaimAttemptLimiter _attemptLimiter;
    private readonly ILogger<ClaimModel> _logger;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public ClaimModel(PrusaConnectService prusaConnectService,
                      TeamService teamService,
                      UserManager<HSUser> userManager,
                      UnitOfWork unitOfWork,
                      ClaimAttemptLimiter attemptLimiter,
                      ILogger<ClaimModel> logger,
                      IStringLocalizer<SharedResource> localiser)
    {
        _prusaConnectService = prusaConnectService;
        _teamService = teamService;
        _userManager = userManager;
        _unitOfWork = unitOfWork;
        _attemptLimiter = attemptLimiter;
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
        [StringLength(200)]
        [Display(Name = "Common_Name")]
        public string? Name { get; set; }

        [StringLength(200)]
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
            ModelState.AddModelError(string.Empty, _localiser["Printers_ClaimLockedOut", FormatWait(remaining)]);

            return Page();
        }

        // Codes are generated in Crockford base32 uppercase (CodeGenerator) and the TemporaryCode
        // lookup has no case-insensitive collation, so a code typed off a printer's screen with
        // different casing, stray whitespace or grouping hyphens would otherwise silently read as
        // unknown. Normalise also applies Crockford's O/I/L substitutions, which is what makes a
        // character misread off a low-resolution LCD still resolve.
        string code = ClaimCode.Normalise(Input.Code);

        try
        {
            // Scoped INSIDE the try, and that placement is the whole point. Declared at method scope
            // it outlives the catch below, so the limiter's save enlisted in a transaction that was
            // then disposed uncommitted - and every failed claim counted as zero. Here the
            // transaction is disposed as the exception leaves this block, before any handler runs,
            // so RecordFailedAttemptAsync writes on its own.
            await using IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

            Printer printer = await _prusaConnectService.ClaimPrinterAsync(
                code, Input.Name, Input.Location, Input.TeamId, CallerResolver.For(user, User));

            // Inside the transaction the claim was made in, so a rollback takes the reset with it.
            await _attemptLimiter.ResetAsync(user, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation("Printer {PrinterUuid} claimed via registration code by user {UserId}.", printer.Uuid, user.Id);

            // Set via the [TempData]-attributed properties below, not PageModel.TempData directly -
            // matching Admin/Invites/IndexModel.OnPostRevokeAsync's pattern. The property names match
            // IndexModel's own [TempData] properties (the default TempData key is the property name),
            // which is what lets the message survive the redirect to a different page.
            StatusMessage = _localiser["Printers_Claimed"];
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

    /// <summary>
    /// Renders a backoff as something worth reading on a form - "45 seconds", "3 minutes" - rather
    /// than a raw <see cref="TimeSpan"/>.
    /// </summary>
    /// <remarks>
    /// Rounds up, so the message never tells someone to retry a moment before they may.
    /// </remarks>
    private string FormatWait(TimeSpan remaining)
    {
        if (remaining < TimeSpan.FromMinutes(1))
        {
            int seconds = (int)Math.Ceiling(remaining.TotalSeconds);

            return seconds == 1 ? _localiser["Printers_ClaimWaitOneSecond"] : _localiser["Printers_ClaimWaitSeconds", seconds];
        }

        int minutes = (int)Math.Ceiling(remaining.TotalMinutes);

        return minutes == 1 ? _localiser["Printers_ClaimWaitOneMinute"] : _localiser["Printers_ClaimWaitMinutes", minutes];
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
