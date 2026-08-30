// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.Accounts;
using Homespool.Host.Localisation;
using Homespool.Host.Pages.Printers;
using Homespool.Model;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace Homespool.Host.Pages.Account.Manage;

/// <summary>
/// Turns two-factor off - against the current authenticator code, not just a live session.
/// </summary>
/// <remarks>
/// <b>The code is required because a live session is exactly what two-factor distrusts.</b> Without
/// it, the walk-up on an unlocked browser that the second factor exists to stop could simply switch
/// the second factor off first. Requiring a current code means weakening the account takes the same
/// credential the account is protected by - the shape the printer-removal confirmation set, backed
/// off through the same per-account limiter, because six digits with unlimited attempts is not a
/// control.
/// </remarks>
[Authorize]
public class Disable2faModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly AttemptLimiter _attemptLimiter;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<Disable2faModel> _logger;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public Disable2faModel(UserManager<HSUser> userManager,
                           AttemptLimiter attemptLimiter,
                           TimeProvider timeProvider,
                           ILogger<Disable2faModel> logger,
                           IStringLocalizer<SharedResource> localiser)
    {
        _userManager = userManager;
        _attemptLimiter = attemptLimiter;
        _timeProvider = timeProvider;
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

    public async Task<IActionResult> OnPostAsync(string code, CancellationToken cancellationToken)
    {
        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        // The backoff is checked before the code is compared, so a locked-out account cannot learn
        // whether its guesses were close by watching which refusal comes back.
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (await _attemptLimiter.RemainingLockoutAsync(user.Id, LimitedAction.DisableTwoFactor, now, cancellationToken)
                is { } remaining)
        {
            StatusMessage = _localiser["TwoFactor_DisableLockedOut", BackoffWait.Format(_localiser, remaining)];

            return RedirectToPage();
        }

        // Authenticator codes only, as on the printer-removal confirmation: a recovery code is for
        // getting back into an account, and spending one here would widen what an unattended session
        // can do to exactly what this exists to stop.
        string typed = (code ?? string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty);

        bool valid = typed.Length > 0
                     && await _userManager.VerifyTwoFactorTokenAsync(
                            user, _userManager.Options.Tokens.AuthenticatorTokenProvider, typed);

        if (!valid)
        {
            await _attemptLimiter.RecordFailedAttemptAsync(
                user.Id, LimitedAction.DisableTwoFactor, now, cancellationToken);

            StatusMessage = _localiser["TwoFactor_DisableCodeInvalid"];

            return RedirectToPage();
        }

        await _attemptLimiter.ResetAsync(user.Id, LimitedAction.DisableTwoFactor, cancellationToken);

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
