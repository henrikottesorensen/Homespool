// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;

namespace Homespool.Host.Pages.Account;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly SignInManager<HSUser> _signInManager;
    private readonly UserManager<HSUser> _userManager;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(SignInManager<HSUser> signInManager, UserManager<HSUser> userManager, ILogger<LoginModel> logger)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; }

    public IList<AuthenticationScheme> ExternalLogins { get; set; }

    public string ReturnUrl { get; set; }

    [TempData]
    public string ErrorMessage { get; set; }

    public class InputModel
    {
        /// <summary>
        /// Either identifier the account has: its username or its email address.
        /// </summary>
        /// <remarks>
        /// One field rather than two, and no <c>[EmailAddress]</c> on it - the attribute was what made
        /// this field mean "address", and it would now reject every username typed into it.
        /// </remarks>
        [Required]
        [Display(Name = "Email or username")]
        public string Login { get; set; }

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; }

        [Display(Name = "Remember me?")]
        public bool RememberMe { get; set; }
    }

    public async Task OnGetAsync(string returnUrl = null)
    {
        if (!string.IsNullOrEmpty(ErrorMessage))
        {
            ModelState.AddModelError(string.Empty, ErrorMessage);
        }

        returnUrl ??= Url.Content("~/");

        // Clear the existing external cookie to ensure a clean login process.
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

        ExternalLogins = await _signInManager.GetExternalAuthenticationSchemesAsync() as IList<AuthenticationScheme>;

        ReturnUrl = returnUrl;
    }

    public async Task<IActionResult> OnPostAsync(string returnUrl = null)
    {
        returnUrl ??= Url.Content("~/");

        ExternalLogins = await _signInManager.GetExternalAuthenticationSchemesAsync() as IList<AuthenticationScheme>;

        if (ModelState.IsValid)
        {
            // lockoutOnFailure: true, changed from the scaffold's default of false (whose comment
            // said "To enable password failures to trigger account lockout, set lockoutOnFailure:
            // true"). Without it a wrong password costs an attacker nothing, and this application has
            // no rate limiting on the login form - so an internet-reachable deployment offered
            // unlimited password guessing against a known account, forever. People do expose
            // self-hosted printer servers to the internet whatever the advice says (OctoPrint's mass
            // exposure is the precedent), so that is the threat model to design for.
            //
            // Identity's defaults apply: 5 failures, then a 5-minute lockout. The tradeoff accepted
            // deliberately is that someone who knows an account's email can keep it locked out; a
            // self-healing five-minute lockout is much the lesser evil, and it is the same tradeoff
            // already live on the 2FA path, which has always counted toward lockout.
            // Resolved by hand because sign-in accepts either identifier, and PasswordSignInAsync's
            // string overload only ever looks at the username. The two namespaces cannot overlap - a
            // username may not contain '@' (HSUser.AllowedUsernameCharacters) - so this order settles
            // nothing that could be ambiguous; it is just the cheaper lookup first.
            HSUser user = await _userManager.FindByNameAsync(Input.Login)
                          ?? await _userManager.FindByEmailAsync(Input.Login);

            if (user is null)
            {
                // Deliberately the same message and the same page as a wrong password: telling an
                // anonymous caller which addresses and usernames exist is the enumeration this form is
                // exposed enough to care about (notes/internet-exposure.md).
                ModelState.AddModelError(string.Empty, "Invalid login attempt.");

                return Page();
            }

            Microsoft.AspNetCore.Identity.SignInResult result = await _signInManager.PasswordSignInAsync(user,
                Input.Password,
                Input.RememberMe,
                lockoutOnFailure: true);
            if (result.Succeeded)
            {
                _logger.LogInformation("User logged in.");

                return LocalRedirect(returnUrl);
            }

            if (result.RequiresTwoFactor)
            {
                return RedirectToPage("./LoginWith2fa", new { ReturnUrl = returnUrl, Input.RememberMe });
            }

            if (result.IsLockedOut)
            {
                _logger.LogWarning("User account locked out.");

                return RedirectToPage("./Lockout");
            }

            ModelState.AddModelError(string.Empty, "Invalid login attempt.");

            return Page();
        }

        // Something failed, redisplay form.
        return Page();
    }
}
