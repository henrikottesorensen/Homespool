// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
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
/// Sets an authenticator app up: the seed and its QR code, and the first code that proves the app
/// holds it - behind a recent proof that the person at the keyboard holds the account.
/// </summary>
/// <remarks>
/// <b>The seed is a credential, so showing it is gated.</b> A live session that could read the
/// account's existing key could enrol it into an app of its own, and every code gate on the account
/// would then be a gate it holds the key to. So the page asks for <see cref="RecentProof"/> before it
/// renders anything, GET included. The code posted back is verified through the same scheme the
/// step-ups use, but it is enrolment - the app proving it holds the seed - and not a proof of the
/// person, which is why it stays beside the gate rather than replacing it.
/// </remarks>
[Authorize]
[RequireRecentProof]
public class EnableAuthenticatorModel : PageModel
{
    private const string AuthenticatorUriFormat = "otpauth://totp/{0}:{1}?secret={2}&issuer={0}&digits=6";

    private readonly UserManager<HSUser> _userManager;
    private readonly UnitOfWork _unitOfWork;
    private readonly ILogger<EnableAuthenticatorModel> _logger;
    private readonly UrlEncoder _urlEncoder;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public EnableAuthenticatorModel(UserManager<HSUser> userManager,
                                    UnitOfWork unitOfWork,
                                    ILogger<EnableAuthenticatorModel> logger,
                                    UrlEncoder urlEncoder,
                                    IStringLocalizer<SharedResource> localiser)
    {
        _userManager = userManager;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _urlEncoder = urlEncoder;
        _localiser = localiser;
    }

    public string SharedKey { get; set; }

    public string AuthenticatorUri { get; set; }

    /// <summary>
    /// The codes minted by a first enable, rendered by that POST's own response; null renders the
    /// setup form.
    /// </summary>
    /// <remarks>
    /// <b>Not carried through a redirect.</b> The codes are stored hashed, so that response is the only
    /// time they exist in the clear, and a redirect would have to carry them in the <c>TempData</c>
    /// cookie - which is not bound to the account and stays decryptable after it has been read, so a
    /// copy of it is ten live credentials that any signed-in session could have the server read out.
    /// A refresh re-submits the form, finds codes already issued and goes to
    /// <c>TwoFactorAuthentication</c> without showing them again.
    /// </remarks>
    public string[] RecoveryCodes { get; private set; }

    /// <summary>The confirmation shown above freshly minted codes, in the same response.</summary>
    public string IssuedMessage { get; private set; }

    [TempData]
    public string StatusMessage { get; set; }

    [BindProperty]
    public InputModel Input { get; set; }

    public class InputModel
    {
        [Required]
        [StringLength(7, ErrorMessage = "Validation_Length", MinimumLength = 6)]
        [DataType(DataType.Text)]
        [Display(Name = "Manage_VerificationCode")]
        public string Code { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        await LoadSharedKeyAndQrCodeUriAsync(user);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        if (!ModelState.IsValid)
        {
            await LoadSharedKeyAndQrCodeUriAsync(user);
            return Page();
        }

        // The code proves the app holds the key just shown; the code scheme verifies it for the
        // signed-in account against that key, two-factor being on or not, and a wrong one backs off
        // the account's step-ups as any wrong step-up does - never its sign-in.
        AuthenticateResult proof = await HttpContext.AuthenticateWithAsync(Schemes.Totp, new TotpStepUpCredential(Input.Code));

        if (!proof.Succeeded)
        {
            ModelState.AddModelError("Input.Code",
                                     proof.Refusal() == SignInRefusal.LockedOut ?
                                         _localiser["Account_LockedOut"] :
                                         _localiser["Manage_VerificationCodeInvalid"]);
            await LoadSharedKeyAndQrCodeUriAsync(user);
            return Page();
        }

        string userId = await _userManager.GetUserIdAsync(user);
        bool issuedRecoveryCodes;

        // Enabling 2FA and minting the recovery codes are two round trips through UserManager, and
        // the state between them is the one worth making unreachable: two-factor on, zero recovery
        // codes. Losing the authenticator device from there means losing the account. Either both
        // land or neither does.
        await using (IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            await _userManager.SetTwoFactorEnabledAsync(user, true);

            issuedRecoveryCodes = await _userManager.CountRecoveryCodesAsync(user) == 0;

            if (issuedRecoveryCodes)
            {
                IEnumerable<string> recoveryCodes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
                RecoveryCodes = recoveryCodes.ToArray();
            }

            await transaction.CommitAsync(cancellationToken);
        }

        _logger.LogInformation("User with ID '{UserId}' has enabled 2FA with an authenticator app.", userId);

        // Rendered only after the commit, so nobody is ever shown recovery codes that were rolled
        // back. Not StatusMessage alongside the codes: that is a TempData property, and a value set on
        // one outlives this response and shows again on the next page.
        if (issuedRecoveryCodes)
        {
            IssuedMessage = _localiser["TwoFactor_AppVerified"];
            return Page();
        }

        StatusMessage = _localiser["TwoFactor_AppVerified"];
        return RedirectToPage("./TwoFactorAuthentication");
    }

    private async Task LoadSharedKeyAndQrCodeUriAsync(HSUser user)
    {
        // Load the authenticator key & QR code URI to display on the form
        string unformattedKey = await _userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(unformattedKey))
        {
            await _userManager.ResetAuthenticatorKeyAsync(user);
            unformattedKey = await _userManager.GetAuthenticatorKeyAsync(user);
        }

        SharedKey = FormatKey(unformattedKey);

        string email = await _userManager.GetEmailAsync(user);
        AuthenticatorUri = GenerateQrCodeUri(email, unformattedKey);
    }

    private string FormatKey(string unformattedKey)
    {
        StringBuilder result = new StringBuilder();
        int currentPosition = 0;
        while (currentPosition + 4 < unformattedKey.Length)
        {
            result.Append(unformattedKey.AsSpan(currentPosition, 4)).Append(' ');
            currentPosition += 4;
        }

        if (currentPosition < unformattedKey.Length)
        {
            result.Append(unformattedKey.AsSpan(currentPosition));
        }

        return result.ToString().ToLowerInvariant();
    }

    private string GenerateQrCodeUri(string email, string unformattedKey)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            AuthenticatorUriFormat,
            _urlEncoder.Encode("Microsoft.AspNetCore.Identity.UI"),
            _urlEncoder.Encode(email),
            unformattedKey);
    }
}
