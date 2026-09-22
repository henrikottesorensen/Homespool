// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

using Homespool.Host.Accounts;
using Homespool.Host.Localisation;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Account;

[AllowAnonymous]
public class ConfirmEmailModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public ConfirmEmailModel(UserManager<HSUser> userManager,
                             IStringLocalizer<SharedResource> localiser)
    {
        _userManager = userManager;
        _localiser = localiser;
    }

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid? userUuid, string? code, CancellationToken cancellationToken)
    {
        if (userUuid == null || code == null)
        {
            return RedirectToPage("/Index");
        }

        HSUser? user = await _userManager.Users.SingleOrDefaultAsync(candidate => candidate.Uuid == userUuid, cancellationToken);
        if (user == null)
        {
            return NotFound();
        }

        string? token = EmailedToken.Decode(code);

        if (token is null)
        {
            // A link broken in transit is a code that cannot work, which this page already has a
            // sentence for. It used to be a 500.
            StatusMessage = _localiser["Account_EmailConfirmFailed"];

            return Page();
        }

        IdentityResult result = await _userManager.ConfirmEmailAsync(user, token);

        StatusMessage = result.Succeeded ?
            _localiser["Account_EmailConfirmed"] :
            _localiser["Account_EmailConfirmFailed"];

        return Page();
    }
}
