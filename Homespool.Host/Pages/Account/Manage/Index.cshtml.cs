// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

using Homespool.Host.Accounts;
using Homespool.Host.Localisation;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace Homespool.Host.Pages.Account.Manage;

[Authorize]
public class IndexModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly SignInManager<HSUser> _signInManager;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public IndexModel(UserManager<HSUser> userManager,
                      SignInManager<HSUser> signInManager,
                      IStringLocalizer<SharedResource> localiser)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _localiser = localiser;
    }

    /// <summary>
    ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    [TempData]
    public string StatusMessage { get; set; }

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
    public class InputModel
    {
        /// <summary>
        /// What the interface calls you, and one of the two things you can sign in with.
        /// </summary>
        [Required]
        [StringLength(HSUser.UsernameMaxLength)]
        [Display(Name = "Account_Username")]
        public string Username { get; set; }
    }

    private async Task LoadAsync(HSUser user)
    {
        Input = new InputModel
        {
            Username = await _userManager.GetUserNameAsync(user),
        };
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

    public async Task<IActionResult> OnPostAsync()
    {
        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(user);
            return Page();
        }

        string userName = await _userManager.GetUserNameAsync(user);
        string requested = Usernames.Normalise(Input.Username.Trim());

        // Ordinal rather than case-insensitive: 'henrik' to 'Henrik' normalises to the same name, so
        // Identity would accept it silently, but it changes what every page renders - which makes it
        // a change the person asked for and should see happen.
        if (!string.Equals(requested, userName, StringComparison.Ordinal))
        {
            IdentityResult setUserName = await _userManager.SetUserNameAsync(user, requested);

            if (!setUserName.Succeeded)
            {
                // Shown rather than swallowed into a status message: "that username is already taken"
                // is the one thing that can go wrong here, and it is entirely actionable. The typed
                // value is deliberately left in the form so it can be edited rather than retyped.
                foreach (IdentityError error in setUserName.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }

                return Page();
            }
        }

        // Re-issues the cookie, which is where the username lives for rendering. Without this the
        // header - and every other reader of the sign-in identity - keeps the old name until the next
        // sign-in.
        await _signInManager.RefreshSignInAsync(user);
        StatusMessage = _localiser["Manage_ProfileUpdated"];
        return RedirectToPage();
    }
}
