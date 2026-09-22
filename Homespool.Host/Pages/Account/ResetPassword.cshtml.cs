// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

using Homespool.Host.Accounts;
using Homespool.Host.Localisation;
using Homespool.Host.RateLimiting;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Account;

[AllowAnonymous] // Carries its own credential in the reset token.
[EnableRateLimiting(RateLimitPolicies.SignIn)]
public class ResetPasswordModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly ApiTokenService _apiTokens;
    private readonly UnitOfWork _unitOfWork;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ILogger<ResetPasswordModel> _logger;

    public ResetPasswordModel(UserManager<HSUser> userManager,
                              ApiTokenService apiTokens,
                              UnitOfWork unitOfWork,
                              IStringLocalizer<SharedResource> localiser,
                              ILogger<ResetPasswordModel> logger)
    {
        _userManager = userManager;
        _apiTokens = apiTokens;
        _unitOfWork = unitOfWork;
        _localiser = localiser;
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [StringLength(100, ErrorMessage = "Validation_Length", MinimumLength = IdentityConfiguration.MinimumPasswordLength)]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [DataType(DataType.Password)]
        [Display(Name = "Account_ConfirmPassword")]
        [Compare("Password", ErrorMessage = "Validation_PasswordMismatch")]
        public string? ConfirmPassword { get; set; }

        [Required]
        public string Code { get; set; } = string.Empty;
    }

    public IActionResult OnGet(string? code = null)
    {
        // Missing and malformed answer the same way: both mean there is no usable code here, and
        // the distinction is not one the person holding the link can act on differently.
        string? token = EmailedToken.Decode(code);

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

        HSUser? user = await _userManager.FindByEmailAsync(Input.Email);
        if (user == null)
        {
            // Refused the way a bad token is, rather than sent to the confirmation page. An address no
            // account holds cannot hold a usable token either, so this is the same answer and not a
            // softer one - and a redirect here, where a known address re-renders, is what told an
            // anonymous caller which addresses exist. The describer is the one the failure below uses,
            // so the two cannot drift into different words.
            ModelState.AddModelError(string.Empty, _userManager.ErrorDescriber.InvalidToken().Description);

            return Page();
        }

        // Revoked here and deliberately nowhere else on the two password paths: recovering by email
        // link is what someone locked out of a compromised account actually does, while a change from
        // a live session already took the current password and is overwhelmingly rotation. Atomic for
        // the same reason - a reset that left the attacker's tokens live would hand back an account
        // that only looks recovered.
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

            await transaction.CommitAsync(cancellationToken);

            // Logged rather than shown: a count of revoked tokens is operator detail, and the person
            // who has just recovered an account cannot act on it. The confirmation page is reached
            // only by a reset that succeeded, so what it says is a choice rather than a constraint.
            _logger.LogInformation("Password reset completed. {RevokedTokenCount} API tokens revoked.", revoked);
        }

        return RedirectToPage("./ResetPasswordConfirmation");
    }
}
