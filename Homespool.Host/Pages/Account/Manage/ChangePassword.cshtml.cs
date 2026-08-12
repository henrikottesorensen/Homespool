// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.Services;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Homespool.Host.Pages.Account.Manage;

public class ChangePasswordModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly SignInManager<HSUser> _signInManager;
    private readonly ApiTokenService _apiTokens;
    private readonly UnitOfWork _unitOfWork;
    private readonly ILogger<ChangePasswordModel> _logger;

    public ChangePasswordModel(UserManager<HSUser> userManager,
                               SignInManager<HSUser> signInManager,
                               ApiTokenService apiTokens,
                               UnitOfWork unitOfWork,
                               ILogger<ChangePasswordModel> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _apiTokens = apiTokens;
        _unitOfWork = unitOfWork;
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
        [Display(Name = "Current password")]
        public string OldPassword { get; set; }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        [Required]
        [StringLength(100, ErrorMessage = "Validation_Length", MinimumLength = 6)]
        [DataType(DataType.Password)]
        [Display(Name = "New password")]
        public string NewPassword { get; set; }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        [DataType(DataType.Password)]
        [Display(Name = "Confirm new password")]
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

        bool hasPassword = await _userManager.HasPasswordAsync(user);
        if (!hasPassword)
        {
            return RedirectToPage("./SetPassword");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
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
            0 => "Your password has been changed.",
            1 => "Your password has been changed. 1 API token was revoked.",
            _ => $"Your password has been changed. {revoked} API tokens were revoked.",
        };

        return RedirectToPage();
    }
}
