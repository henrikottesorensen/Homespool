// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.Accounts;
using Homespool.Host.Localisation;
using Homespool.Host.Services;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace Homespool.Host.Pages.Account.Manage;

[Authorize]
public class ChangePasswordModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly SignInManager<HSUser> _signInManager;
    private readonly ApiTokenService _apiTokens;
    private readonly UnitOfWork _unitOfWork;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ILogger<ChangePasswordModel> _logger;

    public ChangePasswordModel(UserManager<HSUser> userManager,
                               SignInManager<HSUser> signInManager,
                               ApiTokenService apiTokens,
                               UnitOfWork unitOfWork,
                               IStringLocalizer<SharedResource> localiser,
                               ILogger<ChangePasswordModel> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _apiTokens = apiTokens;
        _unitOfWork = unitOfWork;
        _localiser = localiser;
        _logger = logger;
    }

    /// <summary>
    ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    [BindProperty]
    public InputModel Input { get; set; }

    /// <summary>
    ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    [TempData]
    public string StatusMessage { get; set; }

    /// <summary>
    /// Whether this account has a local password at all. False means it signs in with an external
    /// provider, and <b>the view shows an explanation instead of the form</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This used to redirect to <c>./SetPassword</c>, a page that has never existed in this
    /// codebase</b> — Identity.UI took it with the package. An unresolvable <c>RedirectToPage</c>
    /// throws in the executor rather than answering, so the account menu's <i>Password</i> entry
    /// answered <b>500</b> for exactly the accounts an external provider creates. Nothing reached it
    /// before external OIDC was built, because every other creation path sets a password.
    /// </para>
    /// <para>
    /// <b>The page was not restored, deliberately: an account whose credential is the provider does
    /// not get a local one</b> (Henrik, 2026-08-22). So there is nothing to redirect to and nothing
    /// to offer — only something to say. <c>ForgotPassword</c> is gated for the same reason and by the
    /// same test; between them there is no route to a password for such an account.
    /// </para>
    /// </remarks>
    public bool HasPassword { get; private set; }

    /// <summary>
    ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public class InputModel
    {
        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Manage_CurrentPassword")]
        public string OldPassword { get; set; }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        [Required]
        [StringLength(100, ErrorMessage = "Validation_Length", MinimumLength = IdentityConfiguration.MinimumPasswordLength)]
        [DataType(DataType.Password)]
        [Display(Name = "Manage_NewPassword")]
        public string NewPassword { get; set; }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        [DataType(DataType.Password)]
        [Display(Name = "Manage_ConfirmNewPassword")]
        [Compare("NewPassword", ErrorMessage = "Validation_NewPasswordMismatch")]
        public string ConfirmPassword { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        HasPassword = await _userManager.HasPasswordAsync(user);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        HasPassword = await _userManager.HasPasswordAsync(user);

        // Checked on the post as well, not only rendered. The GET withholds the form; that keeps an
        // external account from being offered something it cannot have, and keeps nothing at all from
        // a hand-made request. ChangePasswordAsync would refuse this anyway - there is no old password
        // to match - but as a validation error about the wrong thing, which reads as "you typed your
        // current password incorrectly" to somebody who has never had one.
        if (!HasPassword)
        {
            return Page();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        int revoked;

        // The password change and the revocation are one step, deliberately. The state to make
        // unreachable is "new password, old tokens still live" - which is exactly what someone
        // changing their password because their account is compromised would believe they had
        // escaped. Any return before CommitAsync disposes the transaction uncommitted, rolling both
        // back together.
        await using (IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            IdentityResult changePasswordResult =
                await _userManager.ChangePasswordAsync(user, Input.OldPassword, Input.NewPassword);

            if (!changePasswordResult.Succeeded)
            {
                foreach (IdentityError error in changePasswordResult.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }

                return Page();
            }

            revoked = await _apiTokens.RevokeAllForUserAsync(user.Id, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        // Outside the transaction: re-issuing the cookie is not part of the atomic write, and it is
        // the one step that must not happen if the commit failed.
        await _signInManager.RefreshSignInAsync(user);

        _logger.LogInformation("User changed their password successfully. {RevokedTokenCount} API tokens revoked.", revoked);

        // Silent breakage is the real cost of revoking here, so it is reported - but only when there
        // was something to report. Most accounts hold no tokens and do not need telling so.
        StatusMessage = revoked switch
        {
            0 => _localiser["Manage_PasswordChanged"],
            1 => _localiser["Manage_PasswordChangedOneToken"],
            _ => _localiser["Manage_PasswordChangedTokens", revoked],
        };

        return RedirectToPage();
    }
}
