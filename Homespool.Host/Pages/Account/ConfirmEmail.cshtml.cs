// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.Accounts;
using Homespool.Host.Localisation;
using Homespool.Model;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace Homespool.Host.Pages.Account;

[AllowAnonymous]
public class ConfirmEmailModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly AttemptLimiter _attemptLimiter;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public ConfirmEmailModel(UserManager<HSUser> userManager,
                             AttemptLimiter attemptLimiter,
                             IStringLocalizer<SharedResource> localiser)
    {
        _userManager = userManager;
        _attemptLimiter = attemptLimiter;
        _localiser = localiser;
    }

    [TempData]
    public string StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(string userId, string code, CancellationToken cancellationToken)
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

        string token = EmailedToken.Decode(code);

        if (token is null)
        {
            // A link broken in transit is a code that cannot work, which this page already has a
            // sentence for. It used to be a 500.
            StatusMessage = _localiser["Account_EmailConfirmFailed"];

            return Page();
        }

        IdentityResult result = await _userManager.ConfirmEmailAsync(user, token);

        if (result.Succeeded)
        {
            // A confirmed address is what the counted confirmation emails were for, so the send
            // backoff on ResendEmailConfirmation clears with it.
            await _attemptLimiter.ResetAsync(user.Id, LimitedAction.SendConfirmationEmail, cancellationToken);
        }

        StatusMessage = result.Succeeded ?
            _localiser["Account_EmailConfirmed"] :
            _localiser["Account_EmailConfirmFailed"];

        return Page();
    }
}
