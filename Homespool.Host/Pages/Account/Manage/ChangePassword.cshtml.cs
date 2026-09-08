// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

using Homespool.Host.Authentication;
using Homespool.Host.Accounts;
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
public class ChangePasswordModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly LocalSignIn _signIn;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ILogger<ChangePasswordModel> _logger;

    public ChangePasswordModel(UserManager<HSUser> userManager,
                               LocalSignIn signIn,
                               IStringLocalizer<SharedResource> localiser,
                               ILogger<ChangePasswordModel> logger)
    {
        _userManager = userManager;
        _signIn = signIn;
        _localiser = localiser;
        _logger = logger;
    }

    /// <summary>
    ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    [BindProperty]
    public InputModel Input { get; set; }

    /// <summary>
    ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    [TempData]
    public string StatusMessage { get; set; }

    /// <summary>
    /// Whether this account has a local password at all. False means it signs in with an external
    /// provider, and <b>the view shows an explanation instead of the form</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This used to redirect to <c>./SetPassword</c>, a page that has never existed in this
    /// codebase</b> — Identity.UI took it with the package. An unresolvable <c>RedirectToPage</c>
    /// throws in the executor rather than answering, so the account menu's <i>Password</i> entry
    /// answered <b>500</b> for exactly the accounts an external provider creates. Nothing reached it
    /// before external OIDC was built, because every other creation path sets a password.
    /// </para>
    /// <para>
    /// <b>The page was not restored, deliberately: an account whose credential is the provider does
    /// not get a local one</b> (Henrik, 2026-08-22). So there is nothing to redirect to and nothing
    /// to offer — only something to say. <c>ForgotPassword</c> is gated for the same reason and by the
    /// same test; between them there is no route to a password for such an account.
    /// </para>
    /// </remarks>
    public bool HasPassword { get; private set; }

    /// <summary>
    ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public class InputModel
    {
        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Manage_CurrentPassword")]
        public string OldPassword { get; set; }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        [Required]
        [StringLength(100, ErrorMessage = "Validation_Length", MinimumLength = IdentityConfiguration.MinimumPasswordLength)]
        [DataType(DataType.Password)]
        [Display(Name = "Manage_NewPassword")]
        public string NewPassword { get; set; }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        [DataType(DataType.Password)]
        [Display(Name = "Manage_ConfirmNewPassword")]
        [Compare("NewPassword", ErrorMessage = "Validation_NewPasswordMismatch")]
        public string ConfirmPassword { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        HasPassword = await _userManager.HasPasswordAsync(user);

        return Page();
    }

    /// <summary>
    /// Changes the password, and changes nothing else the account holds.
    /// </summary>
    /// <remarks>
    /// <b>A password change does not revoke this account's API tokens</b>, and the asymmetry with
    /// <c>Account/ResetPassword</c>, which does, is the point. Reaching this form takes the current
    /// password inside a live session, so it is overwhelmingly a rotation by somebody in possession
    /// of their account, and breaking every script they run is a poor answer to routine hygiene. The
    /// reset path is where somebody locked out of a compromised account arrives, and there the tokens
    /// go. An account whose tokens must die while its owner still holds it has two other routes: the
    /// owner revokes them on <c>Manage/ApiTokens</c>, or an administrator does on <c>Admin/Users</c>.
    /// </remarks>
    public async Task<IActionResult> OnPostAsync()
    {
        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        HasPassword = await _userManager.HasPasswordAsync(user);

        // Checked on the post as well, not only rendered. The GET withholds the form; that keeps an
        // external account from being offered something it cannot have, and keeps nothing at all from
        // a hand-made request. ChangePasswordAsync would refuse this anyway - there is no old password
        // to match - but as a validation error about the wrong thing, which reads as "you typed your
        // current password incorrectly" to somebody who has never had one.
        if (!HasPassword)
        {
            return Page();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        IdentityResult changePasswordResult =
            await _userManager.ChangePasswordAsync(user, Input.OldPassword, Input.NewPassword);

        if (!changePasswordResult.Succeeded)
        {
            foreach (IdentityError error in changePasswordResult.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return Page();
        }

        // Counted, not revoked. A passkey is the person's daily sign-in, and losing it on every
        // password change would mean re-enrolling every device every time; and nothing can add
        // one without this password, so a hijacked session cannot have planted one. What a
        // change cannot rule out is a passkey added by somebody who KNEW the password, so the
        // person is told to look, with the list one click away.
        int passkeys = (await _userManager.GetPasskeysAsync(user)).Count;

        await _signIn.RefreshSignInAsync(HttpContext, user);

        _logger.LogInformation("User changed their password successfully.");

        string message = _localiser["Manage_PasswordChanged"];

        if (passkeys > 0)
        {
            message += " " + _localiser["Manage_PasswordChangedPasskeys", passkeys];
        }

        StatusMessage = message;

        return RedirectToPage();
    }
}
