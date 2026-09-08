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
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace Homespool.Host.Pages.Account;

/// <summary>
/// The second half of a two-factor sign-in: the account the password step left pending answers with
/// its authenticator code. The <see cref="Schemes.Totp"/> scheme verifies the code for that account;
/// this page decides what the proof is worth - the session, and a remembered browser if asked.
/// </summary>
[AllowAnonymous]
[EnableRateLimiting(SignInRateLimit.PolicyName)]
public class LoginWith2faModel : PageModel
{
    private readonly LocalSignInRules _rules;
    private readonly LocalSignIn _signIn;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ILogger<LoginWith2faModel> _logger;

    public LoginWith2faModel(LocalSignInRules rules,
                             LocalSignIn signIn,
                             IStringLocalizer<SharedResource> localiser,
                             ILogger<LoginWith2faModel> logger)
    {
        _rules = rules;
        _signIn = signIn;
        _localiser = localiser;
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; }

    public bool RememberMe { get; set; }

    public string ReturnUrl { get; set; }

    public class InputModel
    {
        [Required]
        [StringLength(7, ErrorMessage = "Validation_Length", MinimumLength = 6)]
        [DataType(DataType.Text)]
        [Display(Name = "Account_AuthenticatorCode")]
        public string TwoFactorCode { get; set; }

        [Display(Name = "Account_RememberMachine")]
        public bool RememberMachine { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(bool rememberMe, string returnUrl = null)
    {
        // Ensure the user has gone through the username & password screen first.
        HSUser user = await _rules.PendingTwoFactorAccountAsync(HttpContext);
        if (user is null)
        {
            throw new InvalidOperationException("Unable to load two-factor authentication user.");
        }

        ReturnUrl = returnUrl;
        RememberMe = rememberMe;

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(bool rememberMe, string returnUrl = null)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        returnUrl ??= Url.Content("~/");

        HSUser user = await _rules.PendingTwoFactorAccountAsync(HttpContext);
        if (user is null)
        {
            throw new InvalidOperationException("Unable to load two-factor authentication user.");
        }

        // The scheme verifies the code for the pending account, counts a wrong one toward the lockout
        // and resets the count on a right one; the code is presented as typed and normalised there.
        AuthenticateResult code = await HttpContext.AuthenticateWithAsync(Schemes.Totp, new TotpCredential(Input.TwoFactorCode));

        if (code.Succeeded)
        {
            if (Input.RememberMachine)
            {
                await _signIn.RememberClientAsync(HttpContext, user);
            }

            string loginProvider = await _rules.PendingLoginProviderAsync(HttpContext);
            await _signIn.SignInAsync(HttpContext, code.Principal, rememberMe, loginProvider);

            _logger.LogInformation("User with ID {UserId} logged in with 2fa.", user.Id);

            return LocalRedirect(returnUrl);
        }

        if (code.Refusal() == SignInRefusal.LockedOut)
        {
            _logger.LogWarning("User with ID {UserId} account locked out.", user.Id);

            return RedirectToPage("./Lockout");
        }

        _logger.LogWarning("Invalid authenticator code entered for user with ID {UserId}.", user.Id);
        ModelState.AddModelError(string.Empty, _localiser["Account_InvalidAuthenticatorCode"]);

        return Page();
    }
}
