// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Account.Manage;

/// <summary>
/// Re-keys the authenticator app, for a device that was lost, replaced or is no longer trusted -
/// behind a recent proof that the person at the keyboard holds the account, not just a live session.
/// </summary>
/// <remarks>
/// <para>
/// <b>This turns two-factor authentication off and leaves it off until the new key is verified.</b>
/// It has to: the old key is what the enabled state is enabled <em>against</em>, so keeping the flag
/// on while invalidating the secret would leave an account that demands a code nothing can produce -
/// a lockout with no recovery path but the codes. The page warns about the window rather than hiding
/// it, and lands the reader on <see cref="EnableAuthenticatorModel"/> so closing it is the obvious
/// next step.
/// </para>
/// <para>
/// <b>The proof is <see cref="RecentProof"/>, earned at <c>Account/Reauthenticate</c> with any
/// credential the account holds</b> - which for this page matters: it exists for the person whose
/// authenticator is gone, and they are the one credential-holder who cannot produce a code. The
/// password, a passkey or the account's provider all still work for them. The whole page is gated,
/// GET included, so a person arrives having proved rather than being turned away at the button.
/// </para>
/// <para>
/// <b>The recovery codes go with the key</b>, through <see cref="HSUserManager.ClearTwoFactorAsync"/>.
/// They are the same second factor in another form, and the device that went missing was often kept
/// with them; left standing, they would come back into force when the new key is verified, which
/// mints a fresh set only for an account that has none. The flag, the key and the codes are one
/// save, so no half-done state - two-factor off while the old app still works - can be left behind.
/// </para>
/// <para>
/// <b><see cref="LocalSignIn.RefreshSignInAsync"/> is not optional and runs after the save.</b>
/// Re-keying moves the security stamp, which invalidates the cookie that made this request - without
/// the refresh the reader is signed out mid-flow, and refreshing before the save would mint a cookie
/// for a stamp that a failed save would take away.
/// </para>
/// </remarks>
[Authorize]
[RequireRecentProof]
public class ResetAuthenticatorModel : StatusMessagePageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly LocalSignIn _signIn;
    private readonly ILogger<ResetAuthenticatorModel> _logger;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public ResetAuthenticatorModel(UserManager<HSUser> userManager,
                                   LocalSignIn signIn,
                                   ILogger<ResetAuthenticatorModel> logger,
                                   IStringLocalizer<SharedResource> localiser)
    {
        _userManager = userManager;
        _signIn = signIn;
        _logger = logger;
        _localiser = localiser;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        HSUser? user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        HSUser? user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        string userId = await _userManager.GetUserIdAsync(user);

        HSUserManager users = _userManager as HSUserManager ??
                              throw new NotSupportedException("Resetting the authenticator needs HSUserManager.");

        IdentityResult cleared = await users.ClearTwoFactorAsync(user);
        if (!cleared.Succeeded)
        {
            throw new InvalidOperationException("Unexpected error occurred resetting the authenticator.");
        }

        _logger.LogInformation("User with ID '{UserId}' has reset their authenticator app key.", userId);

        await _signIn.RefreshSignInAsync(HttpContext, user);

        StatusMessage = _localiser["TwoFactor_KeyReset"];
        StatusMessageKind = StatusKind.Success;

        return RedirectToPage("./EnableAuthenticator");
    }
}
