// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
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
/// Mints ten fresh recovery codes - against the account's password, not just a live session.
/// </summary>
/// <remarks>
/// <b>A recovery code is a credential, so minting one is a credential change.</b> Ten of them are ten
/// offline keys to this account that survive a password change, a re-keyed authenticator and a
/// sign-out everywhere; a session somebody else got hold of must not be able to walk away with a set.
/// The proof is the password rather than an authenticator code because the codes are for the person
/// whose authenticator is gone, and <see cref="StepUpGate"/> holds that reasoning and the provider
/// round trip that answers for an account with no password.
/// </remarks>
[Authorize]
public class GenerateRecoveryCodesModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly StepUpGate _stepUp;
    private readonly StepUpText _stepUpText;
    private readonly ExternalSignIn _externalSignIn;
    private readonly ILogger<GenerateRecoveryCodesModel> _logger;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public GenerateRecoveryCodesModel(UserManager<HSUser> userManager,
                                      StepUpGate stepUp,
                                      StepUpText stepUpText,
                                      ExternalSignIn externalSignIn,
                                      ILogger<GenerateRecoveryCodesModel> logger,
                                      IStringLocalizer<SharedResource> localiser)
    {
        _userManager = userManager;
        _stepUp = stepUp;
        _stepUpText = stepUpText;
        _externalSignIn = externalSignIn;
        _logger = logger;
        _localiser = localiser;
    }

    [BindProperty]
    public InputModel Input { get; set; }

    /// <summary>Whether this account proves itself with a password rather than at its provider.</summary>
    public bool UsesPassword { get; private set; }

    /// <summary>The providers a password-less account can be sent to, for the button that sends it.</summary>
    public IReadOnlyList<AuthenticationScheme> Providers { get; private set; } = [];

    public class InputModel
    {
        [DataType(DataType.Password)]
        [Display(Name = "Account_Password")]
        public string Password { get; set; }
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

        await LoadAsync(user);

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

        StepUpResult proof = await _stepUp.ProveAsync(HttpContext, user, Input?.Password);

        if (!proof.Succeeded)
        {
            _logger.LogInformation("Recovery-code generation refused for user {UserId}: {Refusal}.", user.Id, proof.Refusal);

            StatusMessage = await _stepUpText.DescribeAsync(proof, user);

            return RedirectToPage();
        }

        IEnumerable<string> recoveryCodes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        RecoveryCodes = recoveryCodes.ToArray();

        _logger.LogInformation("User with ID '{UserId}' has generated new 2FA recovery codes.", userId);
        StatusMessage = _localiser["TwoFactor_CodesGenerated"];
        return RedirectToPage("./ShowRecoveryCodes");
    }

    /// <summary>
    /// Sends a password-less account to its provider to re-authenticate, coming back to
    /// <see cref="OnGetReauthenticatedAsync"/> on this page - so the proof it earns is scoped here and
    /// cannot be spent on another page.
    /// </summary>
    public async Task<IActionResult> OnPostReauthenticateAsync(string provider)
    {
        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

        string redirectUrl = Url.Page("/Account/Manage/GenerateRecoveryCodes", pageHandler: "Reauthenticated");
        AuthenticationProperties challenge = await _stepUp.ProviderChallengeAsync(user, provider, redirectUrl);

        return challenge is null ? NotFound() : new ChallengeResult(provider, challenge);
    }

    /// <summary>The provider's answer, which becomes the proof the generation above spends.</summary>
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
