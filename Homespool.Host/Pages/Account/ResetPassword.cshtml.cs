// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.Accounts;
using Homespool.Host.Localisation;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace Homespool.Host.Pages.Account;

[AllowAnonymous] // Carries its own credential in the reset token.
public class ResetPasswordModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly ApiTokenService _apiTokens;
    private readonly UnitOfWork _unitOfWork;
    private readonly AttemptLimiter _attemptLimiter;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ILogger<ResetPasswordModel> _logger;

    public ResetPasswordModel(UserManager<HSUser> userManager,
                              ApiTokenService apiTokens,
                              UnitOfWork unitOfWork,
                              AttemptLimiter attemptLimiter,
                              IStringLocalizer<SharedResource> localiser,
                              ILogger<ResetPasswordModel> logger)
    {
        _userManager = userManager;
        _apiTokens = apiTokens;
        _unitOfWork = unitOfWork;
        _attemptLimiter = attemptLimiter;
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
    public class InputModel
    {
        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        [Required]
        [EmailAddress]
        public string Email { get; set; }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        [Required]
        [StringLength(100, ErrorMessage = "Validation_Length", MinimumLength = IdentityConfiguration.MinimumPasswordLength)]
        [DataType(DataType.Password)]
        public string Password { get; set; }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        [DataType(DataType.Password)]
        [Display(Name = "Account_ConfirmPassword")]
        [Compare("Password", ErrorMessage = "Validation_PasswordMismatch")]
        public string ConfirmPassword { get; set; }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        [Required]
        public string Code { get; set; }
    }

    public IActionResult OnGet(string code = null)
    {
        // Missing and malformed answer the same way: both mean there is no usable code here, and
        // the distinction is not one the person holding the link can act on differently.
        string token = EmailedToken.Decode(code);

        if (token is null)
        {
            return BadRequest(_localiser["Account_ResetNeedsCode"].Value);
        }

        Input = new InputModel
        {
            Code = token,
        };

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        HSUser user = await _userManager.FindByEmailAsync(Input.Email);
        if (user == null)
        {
            // Don't reveal that the user does not exist
            return RedirectToPage("./ResetPasswordConfirmation");
        }

        // Revoked here as well as on the change-password page, and this is the more important of the
        // two: recovering by email link is the path someone locked out of a compromised account
        // actually takes. Atomic for the same reason - a reset that left the attacker's tokens live
        // would hand back an account that only looks recovered.
        await using (IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            IdentityResult result = await _userManager.ResetPasswordAsync(user, Input.Code, Input.Password);

            if (!result.Succeeded)
            {
                foreach (IdentityError error in result.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }

                return Page();
            }

            int revoked = await _apiTokens.RevokeAllForUserAsync(user.Id, cancellationToken);

            // A completed reset is what the counted emails were for, so the send backoff clears with
            // it. Inside the transaction deliberately: ResetAsync saves through the ambient context,
            // so a rollback takes the clearing with it.
            await _attemptLimiter.ResetAsync(user.Id, LimitedAction.SendPasswordResetEmail, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            // Logged rather than shown: the confirmation page is reached by anyone who submits the
            // form, including for an address that does not exist, so it cannot say anything specific
            // about an account without becoming an enumeration oracle.
            _logger.LogInformation("Password reset completed. {RevokedTokenCount} API tokens revoked.", revoked);
        }

        return RedirectToPage("./ResetPasswordConfirmation");
    }
}
