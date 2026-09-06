// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace Homespool.Host.Pages.Account;

/// <summary>
/// The way back in without the authenticator: the account the password step left pending redeems
/// one of its recovery codes. The <see cref="Schemes.RecoveryCode"/> scheme redeems it, spending it;
/// this page turns the proof into the session. No remembered browser here - a recovery code is for
/// getting in, not for settling in.
/// </summary>
[AllowAnonymous]
public class LoginWithRecoveryCodeModel : PageModel
{
    private readonly LocalSignInRules _rules;
    private readonly LocalSignIn _signIn;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ILogger<LoginWithRecoveryCodeModel> _logger;

    public LoginWithRecoveryCodeModel(LocalSignInRules rules,
                                      LocalSignIn signIn,
                                      IStringLocalizer<SharedResource> localiser,
                                      ILogger<LoginWithRecoveryCodeModel> logger)
    {
        _rules = rules;
        _signIn = signIn;
        _localiser = localiser;
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; }

    public string ReturnUrl { get; set; }

    public class InputModel
    {
        [BindProperty]
        [Required]
        [DataType(DataType.Text)]
        [Display(Name = "Account_RecoveryCode")]
        public string RecoveryCode { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(string returnUrl = null)
    {
        // Ensure the user has gone through the username & password screen first.
        HSUser user = await _rules.PendingTwoFactorAccountAsync(HttpContext);
        if (user is null)
        {
            throw new InvalidOperationException("Unable to load two-factor authentication user.");
        }

        ReturnUrl = returnUrl;

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string returnUrl = null)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        HSUser user = await _rules.PendingTwoFactorAccountAsync(HttpContext);
        if (user is null)
        {
            throw new InvalidOperationException("Unable to load two-factor authentication user.");
        }

        AuthenticateResult code = await HttpContext.AuthenticateWithAsync(Schemes.RecoveryCode, new RecoveryCodeCredential(Input.RecoveryCode));

        if (code.Succeeded)
        {
            string loginProvider = await _rules.PendingLoginProviderAsync(HttpContext);
            await _signIn.SignInAsync(HttpContext, code.Principal, isPersistent: false, loginProvider);

            _logger.LogInformation("User with ID {UserId} logged in with a recovery code.", user.Id);

            return LocalRedirect(returnUrl ?? Url.Content("~/"));
        }

        if (code.Refusal() == SignInRefusal.LockedOut)
        {
            _logger.LogWarning("User with ID {UserId} account locked out.", user.Id);

            return RedirectToPage("./Lockout");
        }

        _logger.LogWarning("Invalid recovery code entered for user with ID {UserId}", user.Id);
        ModelState.AddModelError(string.Empty, _localiser["Account_InvalidRecoveryCode"]);

        return Page();
    }
}
