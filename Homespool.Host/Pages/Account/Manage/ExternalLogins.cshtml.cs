// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
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

using Homespool.Host.Localisation;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Account.Manage;

/// <summary>
/// The external identity providers linked to this account: what is linked, linking another, and
/// removing one.
/// </summary>
/// <remarks>
/// <para>
/// <b>An account always has at least one way in, and this page is where that invariant is most easily
/// broken.</b> Removing the last external login from an account that has no password would leave it
/// with no credential at all — and, because <c>ChangePassword</c> refuses and <c>ForgotPassword</c> is
/// gated for exactly such an account, no way to obtain one afterwards and no administrator-side reset
/// to fall back on. So the removal <b>asks for a password</b> rather than refusing: the account is
/// swapping one credential for another, not shedding one.
/// </para>
/// <para>
/// <b>That is not a hole in "an external account does not get a local password"</b> (Henrik,
/// 2026-08-22) — it is the one sanctioned transition out of that state, and it costs the provider
/// link in the same step. What the rule refuses is a *second*, parallel credential sitting alongside
/// the provider; what this does is replace the first with the second, atomically. The transaction is
/// what makes that claim true rather than aspirational.
/// </para>
/// <para>
/// <b>The consequence to know about:</b> an account that takes this route is no longer covered by
/// disabling it at the provider. That is inherent in allowing the transition at all, and it is why
/// the alternative — refusing the removal outright — was considered first. <c>user-identity.md</c>
/// carries the reasoning.
/// </para>
/// <para>
/// <b>Linking does not go through the invite gate</b>, and must not: <c>Account/ExternalLogin</c>'s
/// callback decides whether a stranger may have an account, whereas this one attaches a provider to
/// an account that already exists and is signed in. They are different questions, which is why this
/// page carries its own callback rather than reusing that one.
/// </para>
/// </remarks>
[Authorize]
public class ExternalLoginsModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly SignInManager<HSUser> _signInManager;
    private readonly UnitOfWork _unitOfWork;
    private readonly ILogger<ExternalLoginsModel> _logger;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public ExternalLoginsModel(UserManager<HSUser> userManager,
                               SignInManager<HSUser> signInManager,
                               UnitOfWork unitOfWork,
                               ILogger<ExternalLoginsModel> logger,
                               IStringLocalizer<SharedResource> localiser)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _localiser = localiser;
    }

    /// <summary>The providers already attached to this account.</summary>
    public IList<UserLoginInfo> CurrentLogins { get; set; }

    /// <summary>
    /// Registered providers this account has not linked yet — at most one today, since
    /// <c>OidcOptions</c> describes a single provider.
    /// </summary>
    public IList<AuthenticationScheme> OtherLogins { get; set; }

    /// <summary>
    /// Whether the account has a local password, which decides what removing a login costs. With one,
    /// removal is just removal; without, it is the swap described on this class.
    /// </summary>
    public bool HasPassword { get; set; }

    /// <summary>
    /// Whether removing a login would leave the account with no credential, and so has to set a
    /// password in the same step. True only for the <em>last</em> login of a password-less account.
    /// </summary>
    public bool RemovalNeedsAPassword => !HasPassword && CurrentLogins is { Count: <= 1 };

    [BindProperty]
    public InputModel Input { get; set; }

    [TempData]
    public string StatusMessage { get; set; }

    /// <summary>The password an account must set to be allowed to remove its last external login.</summary>
    public class InputModel
    {
        [DataType(DataType.Password)]
        [StringLength(100, ErrorMessage = "Validation_Length", MinimumLength = 6)]
        [Display(Name = "Account_Password")]
        public string NewPassword { get; set; }

        [DataType(DataType.Password)]
        [Display(Name = "Account_ConfirmPassword")]
        [Compare(nameof(NewPassword), ErrorMessage = "Validation_PasswordMismatch")]
        public string ConfirmPassword { get; set; }
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

    /// <summary>Begins linking a provider to the signed-in account.</summary>
    /// <remarks>
    /// The external cookie is cleared first, exactly as the sign-in flow does: a stale one from an
    /// earlier attempt would be picked up by the callback below and linked instead of the identity the
    /// person is about to authenticate as.
    /// </remarks>
    public async Task<IActionResult> OnPostLinkLoginAsync(string provider)
    {
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

        string redirectUrl = Url.Page("./ExternalLogins", pageHandler: "LinkLoginCallback");
        AuthenticationProperties properties =
            _signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl, _userManager.GetUserId(User));

        return new ChallengeResult(provider, properties);
    }

    public async Task<IActionResult> OnGetLinkLoginCallbackAsync()
    {
        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        // Keyed on the signed-in account, so a callback carrying somebody else's external cookie
        // cannot attach their provider identity to this account.
        ExternalLoginInfo info = await _signInManager.GetExternalLoginInfoAsync(user.Id.ToString(CultureInfo.InvariantCulture));
        if (info == null)
        {
            StatusMessage = _localiser["Manage_ExternalLoginLinkError"];

            return RedirectToPage();
        }

        IdentityResult result = await _userManager.AddLoginAsync(user, info);
        if (!result.Succeeded)
        {
            StatusMessage = _localiser["Manage_ExternalLoginLinkError"];

            return RedirectToPage();
        }

        // Clear the external cookie now it has been consumed, so it cannot be replayed.
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

        _logger.LogInformation("Linked the {LoginProvider} login to an existing account.", info.LoginProvider);

        StatusMessage = _localiser["Manage_ExternalLoginLinked"];

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveLoginAsync(string loginProvider, string providerKey,
                                                            CancellationToken cancellationToken)
    {
        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        await LoadAsync(user);

        if (!RemovalNeedsAPassword)
        {
            IdentityResult removed = await _userManager.RemoveLoginAsync(user, loginProvider, providerKey);

            if (!removed.Succeeded)
            {
                StatusMessage = _localiser["Manage_ExternalLoginRemoveError"];

                return RedirectToPage();
            }

            await _signInManager.RefreshSignInAsync(user);

            StatusMessage = _localiser["Manage_ExternalLoginRemoved"];

            return RedirectToPage();
        }

        // The swap. ModelState is only consulted on this branch, because the password fields are only
        // rendered on it - validating them unconditionally would refuse an ordinary removal for not
        // supplying a password it never asked for.
        if (!ModelState.IsValid || string.IsNullOrEmpty(Input?.NewPassword))
        {
            return Page();
        }

        // Two round trips, and the half-done states are both bad: a password added but the provider
        // still linked is the parallel credential the rule refuses, and a provider removed but no
        // password set is the lockout this whole branch exists to prevent. Any return before
        // CommitAsync disposes the transaction uncommitted and undoes both.
        await using (IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            IdentityResult added = await _userManager.AddPasswordAsync(user, Input.NewPassword);

            if (!added.Succeeded)
            {
                AddErrors(added);

                return Page();
            }

            IdentityResult removed = await _userManager.RemoveLoginAsync(user, loginProvider, providerKey);

            if (!removed.Succeeded)
            {
                AddErrors(removed);

                return Page();
            }

            await transaction.CommitAsync(cancellationToken);
        }

        // After the commit, and required: both writes move the security stamp, so the cookie that made
        // this request is stale. Refreshing before would mint one for a stamp a rollback would remove.
        await _signInManager.RefreshSignInAsync(user);

        _logger.LogInformation("Removed the {LoginProvider} login and set a password in its place.", loginProvider);

        StatusMessage = _localiser["Manage_ExternalLoginSwappedForPassword"];

        return RedirectToPage();
    }

    private async Task LoadAsync(HSUser user)
    {
        CurrentLogins = await _userManager.GetLoginsAsync(user);

        OtherLogins = (await _signInManager.GetExternalAuthenticationSchemesAsync())
                      .Where(scheme => CurrentLogins.All(login => login.LoginProvider != scheme.Name))
                      .ToList();

        HasPassword = await _userManager.HasPasswordAsync(user);
    }

    private void AddErrors(IdentityResult result)
    {
        foreach (IdentityError error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, error.Description);
        }
    }
}
