// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

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

[Authorize]
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

    /// <summary>
    ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public bool HasAuthenticator { get; set; }

    /// <summary>
    ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public int RecoveryCodesLeft { get; set; }

    /// <summary>
    ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    [BindProperty]
    public bool Is2faEnabled { get; set; }

    /// <summary>
    ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public bool IsMachineRemembered { get; set; }

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

        HasAuthenticator = await _userManager.GetAuthenticatorKeyAsync(user) != null;
        Is2faEnabled = await _userManager.GetTwoFactorEnabledAsync(user);
        IsMachineRemembered = await _rules.IsTwoFactorClientRememberedAsync(HttpContext, user);
        RecoveryCodesLeft = await _userManager.CountRecoveryCodesAsync(user);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        HSUser user = await _userManager.GetUserAsync(User);
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
