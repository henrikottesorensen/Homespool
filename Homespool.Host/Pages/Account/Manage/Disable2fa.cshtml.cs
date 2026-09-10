// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System;
using System.Threading.Tasks;

using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
using Homespool.Host.Pages.Printers;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace Homespool.Host.Pages.Account.Manage;

/// <summary>
/// Turns two-factor off - against the current authenticator code, not just a live session.
/// </summary>
/// <remarks>
/// <para>
/// <b>The code is required because a live session is exactly what two-factor distrusts.</b> Without
/// it, the walk-up on an unlocked browser that the second factor exists to stop could simply switch
/// the second factor off first. Requiring a current code means weakening the account through this
/// page takes the same credential the account is protected by - the shape the printer-removal
/// confirmation set, through the same code scheme, which backs a wrong code off with every other
/// step-up: six digits with unlimited attempts is not a control.
/// </para>
/// <para>
/// <b>What that does not buy today, and it is the reason to be careful changing either page.</b>
/// <see cref="EnableAuthenticatorModel"/>'s GET renders the existing authenticator seed and its QR
/// code to any live session, with no step-up of its own and no check that two-factor is currently
/// off. So the walk-up this page turns away can enrol that seed into its own app one screen over and
/// come back and satisfy this one. Until that page refuses to re-display a seed the account already
/// has, the code asked for here proves possession of a secret a session-holder can help themselves
/// to, and this guard is worth what the neighbouring page leaves it worth.
/// </para>
/// <para>
/// <b>The page beside it asks for a different credential, not for nothing.</b>
/// <see cref="ResetAuthenticatorModel"/> - the button next to this one on
/// <c>TwoFactorAuthentication</c> - reaches the same state, the flag off, and demands the account's
/// password rather than a code: it exists for the person whose authenticator is gone, who is the one
/// credential-holder that cannot produce one. Both doors are shut; they take different keys.
/// </para>
/// </remarks>
[Authorize]
public class Disable2faModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly LocalSignInRules _rules;
    private readonly ILogger<Disable2faModel> _logger;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public Disable2faModel(UserManager<HSUser> userManager,
                           LocalSignInRules rules,
                           ILogger<Disable2faModel> logger,
                           IStringLocalizer<SharedResource> localiser)
    {
        _userManager = userManager;
        _rules = rules;
        _logger = logger;
        _localiser = localiser;
    }

    /// <summary>
    ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    [TempData]
    public string StatusMessage { get; set; }

    public async Task<IActionResult> OnGet()
    {
        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        if (!await _userManager.GetTwoFactorEnabledAsync(user))
        {
            throw new InvalidOperationException($"Cannot disable 2FA for user as it's not currently enabled.");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string code)
    {
        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        // Authenticator codes only, as on the printer-removal confirmation: a recovery code is for
        // getting back into an account, and spending one here would widen what an unattended session
        // can do to exactly what this exists to stop. The code scheme verifies it for the signed-in
        // account, backs a wrong one off with every other step-up, and refuses a backed-off or
        // locked-out account first.
        AuthenticateResult stepUp = await HttpContext.AuthenticateWithAsync(Schemes.Totp, new TotpStepUpCredential(code));

        if (!stepUp.Succeeded)
        {
            StatusMessage = stepUp.Refusal() == SignInRefusal.LockedOut
                ? _localiser["TwoFactor_DisableLockedOut", BackoffWait.Format(_localiser, stepUp.RetryAfter() ?? TimeSpan.Zero)]
                : _localiser["TwoFactor_DisableCodeInvalid"];

            return RedirectToPage();
        }

        IdentityResult disable2faResult = await _userManager.SetTwoFactorEnabledAsync(user, false);
        if (!disable2faResult.Succeeded)
        {
            throw new InvalidOperationException($"Unexpected error occurred disabling 2FA.");
        }

        _logger.LogInformation("User with ID '{UserId}' has disabled 2fa.", _userManager.GetUserId(User));
        StatusMessage = _localiser["TwoFactor_Disabled"];
        return RedirectToPage("./TwoFactorAuthentication");
    }
}
