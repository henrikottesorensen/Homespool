// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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

[Authorize]
[NoRecentProof("Reads the state and forgets this browser, which only makes the next sign-in ask more.")]
public class TwoFactorAuthenticationModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly LocalSignInRules _rules;
    private readonly LocalSignIn _signIn;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ILogger<TwoFactorAuthenticationModel> _logger;

    public TwoFactorAuthenticationModel(UserManager<HSUser> userManager,
                                        LocalSignInRules rules,
                                        LocalSignIn signIn,
                                        IStringLocalizer<SharedResource> localiser,
                                        ILogger<TwoFactorAuthenticationModel> logger)
    {
        _userManager = userManager;
        _rules = rules;
        _signIn = signIn;
        _localiser = localiser;
        _logger = logger;
    }

    public bool HasAuthenticator { get; set; }

    public int RecoveryCodesLeft { get; set; }

    [BindProperty]
    public bool Is2faEnabled { get; set; }

    public bool IsMachineRemembered { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        HSUser? user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        HasAuthenticator = await _userManager.GetAuthenticatorKeyAsync(user) != null;
        Is2faEnabled = await _userManager.GetTwoFactorEnabledAsync(user);
        IsMachineRemembered = await _rules.IsTwoFactorClientRememberedAsync(HttpContext, user);
        RecoveryCodesLeft = await _userManager.CountRecoveryCodesAsync(user);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        HSUser? user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        await _signIn.ForgetClientAsync(HttpContext);
        StatusMessage =
            _localiser["Manage_BrowserForgotten"];
        return RedirectToPage();
    }
}
