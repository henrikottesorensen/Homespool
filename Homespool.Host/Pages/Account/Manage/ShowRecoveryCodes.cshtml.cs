// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Homespool.Host.Accounts;
using Homespool.Host.Authentication;

namespace Homespool.Host.Pages.Account.Manage;

/// <summary>
/// The one screen that ever shows a recovery code in the clear.
/// </summary>
/// <remarks>
/// <para>
/// <b>The codes arrive by <see cref="TempDataAttribute"/> and are gone after this request.</b> Nothing
/// can render them a second time - both callers (<see cref="EnableAuthenticatorModel"/> and
/// <see cref="GenerateRecoveryCodesModel"/>) mint the codes, hand them over in a redirect, and have no
/// way to answer a reader who missed them; the only recourse is to generate a fresh set, which
/// invalidates the old. That is why a caller must not redirect here until its own write has committed,
/// and why a refresh lands on <c>TwoFactorAuthentication</c> rather than an empty list pretending to
/// be an answer.
/// </para>
/// <para>
/// <b>This is also the only moment the codes exist in the clear.</b> <see cref="HSUserStore"/> keeps
/// only their hashes, so nothing - this page, an administrator, the database - can recover a code once
/// the request that showed it is over.
/// </para>
/// <para>
/// The page therefore has no handler but <see cref="OnGet"/>: there is nothing to post, and the only
/// decision it makes is whether it has anything to say.
/// </para>
/// </remarks>
[Authorize]
[NoRecentProof("Shows codes the gated page just minted; it mints nothing itself.")]
public class ShowRecoveryCodesModel : PageModel
{
    /// <summary>The codes to display, handed over by the page that generated them.</summary>
    [TempData]
    public string[] RecoveryCodes { get; set; }

    [TempData]
    public string StatusMessage { get; set; }

    /// <summary>
    /// Shows the codes, or sends a reader with none - a direct visit, a refresh, a back button -
    /// back to the two-factor page rather than rendering an empty page that looks like a failure.
    /// </summary>
    public IActionResult OnGet()
    {
        if (RecoveryCodes == null || RecoveryCodes.Length == 0)
        {
            return RedirectToPage("./TwoFactorAuthentication");
        }

        return Page();
    }
}
