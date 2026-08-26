// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

using Homespool.Host.Localisation;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Account.Manage;

/// <summary>
/// Re-keys the authenticator app, for a device that was lost, replaced or is no longer trusted.
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
/// <b>The two writes are one transaction</b>, for the reason <c>notes/transactions.md</c> gives -
/// several round trips, not several entities. The state worth making unreachable here is the
/// half-done one: two-factor off while the old app still works, which reads to the account holder as
/// a reset that did nothing while quietly having removed their second factor.
/// </para>
/// <para>
/// <b><see cref="SignInManager{TUser}.RefreshSignInAsync"/> is not optional and runs after the
/// commit.</b> Re-keying moves the security stamp, which invalidates the cookie that made this
/// request - without the refresh the reader is signed out mid-flow, and refreshing before the commit
/// would mint a cookie for a stamp that a rollback would take away.
/// </para>
/// </remarks>
[Authorize]
public class ResetAuthenticatorModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly SignInManager<HSUser> _signInManager;
    private readonly UnitOfWork _unitOfWork;
    private readonly ILogger<ResetAuthenticatorModel> _logger;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public ResetAuthenticatorModel(UserManager<HSUser> userManager,
                                   SignInManager<HSUser> signInManager,
                                   UnitOfWork unitOfWork,
                                   ILogger<ResetAuthenticatorModel> logger,
                                   IStringLocalizer<SharedResource> localiser)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _localiser = localiser;
    }

    [TempData]
    public string StatusMessage { get; set; }

    public async Task<IActionResult> OnGet()
    {
        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        HSUser user = await _userManager.GetUserAsync(User);
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

        await _signInManager.RefreshSignInAsync(user);

        StatusMessage = _localiser["TwoFactor_KeyReset"];

        return RedirectToPage("./EnableAuthenticator");
    }
}
