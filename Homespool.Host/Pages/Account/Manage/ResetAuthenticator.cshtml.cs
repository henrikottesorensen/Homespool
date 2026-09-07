// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
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
/// against the account's password, not just a live session.
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
/// <b>The password, and not the authenticator code <see cref="Disable2faModel"/> asks for.</b> Both
/// pages end with the flag off, so a code on one and nothing on the other left the stricter page
/// decorative: a walk-up on an unlocked browser that found this page shut simply used that one. But
/// the code cannot be the proof <i>here</i> - this page exists for the person whose device is gone,
/// and they are the one credential-holder who cannot produce it. The password is what survives a lost
/// phone, so the password is what is asked for; <see cref="StepUpGate"/> carries the reasoning and the
/// provider round trip that answers for an account which has none.
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
public class ResetAuthenticatorModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly LocalSignIn _signIn;
    private readonly StepUpGate _stepUp;
    private readonly StepUpText _stepUpText;
    private readonly ExternalSignIn _externalSignIn;
    private readonly UnitOfWork _unitOfWork;
    private readonly ILogger<ResetAuthenticatorModel> _logger;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public ResetAuthenticatorModel(UserManager<HSUser> userManager,
                                   LocalSignIn signIn,
                                   StepUpGate stepUp,
                                   StepUpText stepUpText,
                                   ExternalSignIn externalSignIn,
                                   UnitOfWork unitOfWork,
                                   ILogger<ResetAuthenticatorModel> logger,
                                   IStringLocalizer<SharedResource> localiser)
    {
        _userManager = userManager;
        _signIn = signIn;
        _stepUp = stepUp;
        _stepUpText = stepUpText;
        _externalSignIn = externalSignIn;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _localiser = localiser;
    }

    [BindProperty]
    public InputModel Input { get; set; }

    /// <summary>
    /// Whether this account proves itself with a password. False puts the provider round trip on the
    /// page instead, because there is no password to ask for.
    /// </summary>
    public bool UsesPassword { get; private set; }

    /// <summary>The providers a password-less account can be sent to, for the button that sends it.</summary>
    public IReadOnlyList<AuthenticationScheme> Providers { get; private set; } = [];

    [TempData]
    public string StatusMessage { get; set; }

    public class InputModel
    {
        [DataType(DataType.Password)]
        [Display(Name = "Account_Password")]
        public string Password { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        await LoadAsync(user);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        StepUpResult proof = await _stepUp.ProveAsync(HttpContext, user, Input?.Password);

        if (!proof.Succeeded)
        {
            _logger.LogInformation("Authenticator reset refused for user {UserId}: {Refusal}.", user.Id, proof.Refusal);

            StatusMessage = _stepUpText.Describe(proof);

            return RedirectToPage();
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

    /// <summary>
    /// Sends a password-less account to its provider to re-authenticate, coming back to
    /// <see cref="OnGetReauthenticatedAsync"/> on this page - so the proof it earns is scoped to this
    /// page and cannot be spent on another.
    /// </summary>
    public async Task<IActionResult> OnPostReauthenticateAsync(string provider)
    {
        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

        string redirectUrl = Url.Page("/Account/Manage/ResetAuthenticator", pageHandler: "Reauthenticated");
        AuthenticationProperties challenge = await _stepUp.ProviderChallengeAsync(user, provider, redirectUrl);

        return challenge is null ? NotFound() : new ChallengeResult(provider, challenge);
    }

    /// <summary>The provider's answer, which becomes the proof the reset below spends.</summary>
    public async Task<IActionResult> OnGetReauthenticatedAsync()
    {
        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        StatusMessage = _stepUpText.Describe(await _stepUp.RecordProviderProofAsync(HttpContext, user));

        return RedirectToPage();
    }

    private async Task LoadAsync(HSUser user)
    {
        UsesPassword = await _stepUp.UsesPasswordAsync(user);
        Providers = UsesPassword ? [] : await _externalSignIn.ProvidersAsync();
    }
}
