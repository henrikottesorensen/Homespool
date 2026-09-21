// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
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
/// <para>
/// <b>The codes are rendered by the POST that mints them, not carried through a redirect.</b> They
/// are stored hashed, so that response is the only time they exist in the clear, and a redirect
/// would have to carry them in the <c>TempData</c> cookie - which is not bound to the account, stays
/// decryptable after it has been read, and so is a portable copy of ten live credentials. Refreshing
/// the response re-submits the form and mints a fresh set, which replaces the one shown and is shown
/// in full itself, so nothing is lost by it.
/// </para>
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

    /// <summary>The codes just minted, set only by a successful POST; null renders the form.</summary>
    public string[]? RecoveryCodes { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    /// <summary>The confirmation shown above freshly minted codes, in the same response.</summary>
    public string? IssuedMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        HSUser? user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        // Nothing links here with two-factor off, but a typed URL or a tab left open while it was
        // turned off elsewhere still arrives. The two-factor page shows the real state, so that is
        // where the reader goes, rather than to an error page for a state they did nothing wrong in.
        bool isTwoFactorEnabled = await _userManager.GetTwoFactorEnabledAsync(user);
        if (!isTwoFactorEnabled)
        {
            return RedirectToPage("./TwoFactorAuthentication");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        HSUser? user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        bool isTwoFactorEnabled = await _userManager.GetTwoFactorEnabledAsync(user);
        string userId = await _userManager.GetUserIdAsync(user);
        if (!isTwoFactorEnabled)
        {
            return RedirectToPage("./TwoFactorAuthentication");
        }

        // Null means the store refused the update, so no codes were saved to show.
        IEnumerable<string> recoveryCodes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10) ??
            throw new InvalidOperationException("The new recovery codes could not be stored.");
        RecoveryCodes = recoveryCodes.ToArray();

        _logger.LogInformation("User with ID '{UserId}' has generated new 2FA recovery codes.", userId);

        // Not StatusMessage: that is a TempData property, and a value set on one would outlive this
        // response and show again on the next page.
        IssuedMessage = _localiser["TwoFactor_CodesGenerated"];
        return Page();
    }
}
