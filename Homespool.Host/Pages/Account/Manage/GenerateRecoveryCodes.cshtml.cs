// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace Homespool.Host.Pages.Account.Manage;

/// <summary>
/// Mints ten fresh recovery codes - behind a recent proof that the person at the keyboard holds the
/// account, not just a live session.
/// </summary>
/// <remarks>
/// <b>A recovery code is a credential, so minting one is a credential change.</b> Ten of them are ten
/// offline keys to this account that survive a password change, a re-keyed authenticator and a
/// sign-out everywhere; a session somebody else got hold of must not be able to walk away with a set.
/// The proof is <see cref="RecentProof"/>, earned at <c>Account/Reauthenticate</c> with any credential
/// the account holds - and not the authenticator code in particular, because the codes are for the
/// person whose authenticator is gone. The whole page is gated, GET included.
/// </remarks>
[Authorize]
[RequireRecentProof]
public class GenerateRecoveryCodesModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly ILogger<GenerateRecoveryCodesModel> _logger;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public GenerateRecoveryCodesModel(UserManager<HSUser> userManager,
                                      ILogger<GenerateRecoveryCodesModel> logger,
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
    public string[] RecoveryCodes { get; set; }

    /// <summary>
    ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    [TempData]
    public string StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        bool isTwoFactorEnabled = await _userManager.GetTwoFactorEnabledAsync(user);
        if (!isTwoFactorEnabled)
        {
            throw new InvalidOperationException($"Cannot generate recovery codes for user because they do not have 2FA enabled.");
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

        bool isTwoFactorEnabled = await _userManager.GetTwoFactorEnabledAsync(user);
        string userId = await _userManager.GetUserIdAsync(user);
        if (!isTwoFactorEnabled)
        {
            throw new InvalidOperationException($"Cannot generate recovery codes for user as they do not have 2FA enabled.");
        }

        IEnumerable<string> recoveryCodes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        RecoveryCodes = recoveryCodes.ToArray();

        _logger.LogInformation("User with ID '{UserId}' has generated new 2FA recovery codes.", userId);
        StatusMessage = _localiser["TwoFactor_CodesGenerated"];
        return RedirectToPage("./ShowRecoveryCodes");
    }
}
