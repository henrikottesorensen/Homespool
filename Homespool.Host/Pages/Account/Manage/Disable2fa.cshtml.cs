// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Account.Manage;

/// <summary>
/// Turns two-factor off - behind a recent proof that the person at the keyboard holds the account,
/// not just a live session.
/// </summary>
/// <remarks>
/// <para>
/// <b>A proof is required because a live session is exactly what two-factor distrusts.</b> Without
/// it, the walk-up on an unlocked browser that the second factor exists to stop could simply switch
/// the second factor off first. The proof is <see cref="RecentProof"/>, earned at
/// <c>Account/Reauthenticate</c> with any credential the account holds, and the whole page is gated,
/// GET included.
/// </para>
/// <para>
/// <b>Any credential, not the authenticator code in particular.</b> This page once asked for the
/// code while <see cref="ResetAuthenticatorModel"/> next door - which reaches the same state, the
/// flag off - took the password, so the stricter door was decorative. Both now take the one proof,
/// and the code is not it: the reset exists for the person whose device is gone, who cannot produce
/// one, and a proof that the reset accepts and this page refuses would only send them next door.
/// </para>
/// </remarks>
[Authorize]
[RequireRecentProof]
public class Disable2faModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly ILogger<Disable2faModel> _logger;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public Disable2faModel(UserManager<HSUser> userManager,
                           ILogger<Disable2faModel> logger,
                           IStringLocalizer<SharedResource> localiser)
    {
        _userManager = userManager;
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

    public async Task<IActionResult> OnPostAsync()
    {
        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
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
