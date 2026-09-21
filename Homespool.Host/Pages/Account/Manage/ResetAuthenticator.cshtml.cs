// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
using Homespool.Host.Services;
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
/// <b>The two writes are one transaction</b>, because it is several round trips rather than
/// several entities. The state worth making unreachable here is the
/// half-done one: two-factor off while the old app still works, which reads to the account holder as
/// a reset that did nothing while quietly having removed their second factor.
/// </para>
/// <para>
/// <b><see cref="LocalSignIn.RefreshSignInAsync"/> is not optional and runs after the
/// commit.</b> Re-keying moves the security stamp, which invalidates the cookie that made this
/// request - without the refresh the reader is signed out mid-flow, and refreshing before the commit
/// would mint a cookie for a stamp that a rollback would take away.
/// </para>
/// </remarks>
[Authorize]
[RequireRecentProof]
public class ResetAuthenticatorModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly LocalSignIn _signIn;
    private readonly UnitOfWork _unitOfWork;
    private readonly ILogger<ResetAuthenticatorModel> _logger;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public ResetAuthenticatorModel(UserManager<HSUser> userManager,
                                   LocalSignIn signIn,
                                   UnitOfWork unitOfWork,
                                   ILogger<ResetAuthenticatorModel> logger,
                                   IStringLocalizer<SharedResource> localiser)
    {
        _userManager = userManager;
        _signIn = signIn;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _localiser = localiser;
    }

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        HSUser? user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        string userId = await _userManager.GetUserIdAsync(user);

        await using (IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            await _userManager.SetTwoFactorEnabledAsync(user, false);
            await _userManager.ResetAuthenticatorKeyAsync(user);

            await transaction.CommitAsync(cancellationToken);
        }

        _logger.LogInformation("User with ID '{UserId}' has reset their authenticator app key.", userId);

        await _signIn.RefreshSignInAsync(HttpContext, user);

        StatusMessage = _localiser["TwoFactor_KeyReset"];

        return RedirectToPage("./EnableAuthenticator");
    }
}
