// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

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
/// The way back in without the authenticator: the account the password step left pending redeems
/// one of its recovery codes. The <see cref="Schemes.RecoveryCode"/> scheme redeems it, spending it;
/// this page turns the proof into the session. No remembered browser here - a recovery code is for
/// getting in, not for settling in.
/// </summary>
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.SignIn)]
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

    /// <summary>The sentence the login page shows when this page sends somebody back to it.</summary>
    [TempData]
    public string ErrorMessage { get; set; }

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
        HSUser user = await _rules.PendingTwoFactorAccountAsync(HttpContext);
        if (user is null)
        {
            return SignInAgain(returnUrl);
        }

        ReturnUrl = returnUrl;

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string returnUrl = null)
    {
        // Before the form is validated: a code field that is empty is not worth correcting when
        // nothing is pending to present it for.
        HSUser user = await _rules.PendingTwoFactorAccountAsync(HttpContext);
        if (user is null)
        {
            return SignInAgain(returnUrl);
        }

        if (!ModelState.IsValid)
        {
            return Page();
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

    /// <summary>
    /// Back to the password step, saying why. Nothing is pending on this browser: the short-lived
    /// cookie the password step wrote ran out while the person looked for their recovery codes, or
    /// it was never written. The two look the same from here, and the answer to both is the login page.
    /// </summary>
    private RedirectToPageResult SignInAgain(string returnUrl)
    {
        ErrorMessage = _localiser["Account_TwoFactorSignInExpired"];

        return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
    }
}
