// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.Text;
using System.Threading.Tasks;

using Homespool.Host.Localisation;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;

namespace Homespool.Host.Pages.Account;

[AllowAnonymous]
public class ConfirmEmailModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public ConfirmEmailModel(UserManager<HSUser> userManager, IStringLocalizer<SharedResource> localiser)
    {
        _userManager = userManager;
        _localiser = localiser;
    }

    [TempData]
    public string StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(string userId, string code)
    {
        if (userId == null || code == null)
        {
            return RedirectToPage("/Index");
        }

        HSUser user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{userId}'.");
        }

        code = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
        IdentityResult result = await _userManager.ConfirmEmailAsync(user, code);
        StatusMessage = result.Succeeded ?
            _localiser["Account_EmailConfirmed"] :
            _localiser["Account_EmailConfirmFailed"];

        return Page();
    }
}
