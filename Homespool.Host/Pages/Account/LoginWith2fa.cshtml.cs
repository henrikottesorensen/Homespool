// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
using Homespool.Host.RateLimiting;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Account;

/// <summary>
/// The second half of a two-factor sign-in: the account the password step left pending answers with
/// its authenticator code. The <see cref="Schemes.Totp"/> scheme verifies the code for that account;
/// this page decides what the proof is worth - the session, and a remembered browser if asked.
/// </summary>
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.SignIn)]
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
    public InputModel Input { get; set; } = new();

    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }

    /// <summary>The sentence the login page shows when this page sends somebody back to it.</summary>
    [TempData]
    public string? ErrorMessage { get; set; }

    public class InputModel
    {
        [Required]
        [StringLength(7, ErrorMessage = "Validation_Length", MinimumLength = 6)]
        [DataType(DataType.Text)]
        [Display(Name = "Account_AuthenticatorCode")]
        public string TwoFactorCode { get; set; } = string.Empty;

        [Display(Name = "Account_RememberMachine")]
        public bool RememberMachine { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(bool rememberMe, string? returnUrl = null)
    {
        HSUser? user = await _rules.PendingTwoFactorAccountAsync(HttpContext);
        if (user is null)
        {
            return SignInAgain(returnUrl);
        }

        ReturnUrl = returnUrl;
        RememberMe = rememberMe;

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(bool rememberMe, string? returnUrl = null)
    {
        // Before the form is validated: a code field that is empty or malformed is not worth
        // correcting when nothing is pending to present it for.
        HSUser? user = await _rules.PendingTwoFactorAccountAsync(HttpContext);
        if (user is null)
        {
            return SignInAgain(returnUrl);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        returnUrl ??= Url.Content("~/");

        // The scheme verifies the code for the pending account, counts a wrong one toward the lockout
        // and resets the count on a right one; the code is presented as typed and normalised there.
        AuthenticateResult code = await HttpContext.AuthenticateWithAsync(Schemes.Totp, new TotpCredential(Input.TwoFactorCode));

        if (code.Succeeded)
        {
            if (Input.RememberMachine)
            {
                await _signIn.RememberClientAsync(HttpContext, user);
            }

            string? loginProvider = await _rules.PendingLoginProviderAsync(HttpContext);
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

    /// <summary>
    /// Back to the password step, saying why. Nothing is pending on this browser: the short-lived
    /// cookie the password step wrote ran out while the person fetched their authenticator, or it
    /// was never written. The two look the same from here, and the answer to both is the login page.
    /// </summary>
    private RedirectToPageResult SignInAgain(string? returnUrl)
    {
        ErrorMessage = _localiser["Account_TwoFactorSignInExpired"];

        return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
    }
}
