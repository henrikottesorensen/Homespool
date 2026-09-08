using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Admin;

/// <summary>
/// Where an administrator proves themselves before the administration screens will act: one
/// password, once, good for <see cref="AdminElevation.Window"/> of work.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own page rather than a field on each screen.</b> A password field beside every button is
/// retyped until it stops being read, and on a page about somebody else's account it asks a
/// genuinely confusing question — whose password is this? Here there is only one account it could
/// mean, and the page says so.
/// </para>
/// <para>
/// <b>The proving is <see cref="StepUpGate"/>'s</b>, unchanged and shared with the four account
/// pages: the password where the administrator has one, a fresh round trip to their provider where
/// they have none. What is different here is what a proof buys — a window over a section, rather
/// than one act.
/// </para>
/// </remarks>
[Authorize(Roles = AdminBootstrap.AdminRole)]
[NoAdminElevation("This is where an elevation is earned; requiring one would redirect it to itself.")]
public class ChallengeModel : PageModel
{
    private readonly UserManager<HSUser> _users;
    private readonly AdminElevation _elevation;
    private readonly StepUpGate _stepUp;
    private readonly StepUpText _stepUpText;
    private readonly ExternalSignIn _externalSignIn;

    public ChallengeModel(UserManager<HSUser> users,
                          AdminElevation elevation,
                          StepUpGate stepUp,
                          StepUpText stepUpText,
                          ExternalSignIn externalSignIn)
    {
        _users = users;
        _elevation = elevation;
        _stepUp = stepUp;
        _stepUpText = stepUpText;
        _externalSignIn = externalSignIn;
    }

    /// <summary>Where the administrator was heading when they were asked.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    /// <summary>Whether this administrator proves themselves with a password rather than at a provider.</summary>
    public bool UsesPassword { get; private set; }

    /// <summary>The providers a password-less administrator can be sent to.</summary>
    public IReadOnlyList<AuthenticationScheme> Providers { get; private set; } = [];

    public class InputModel
    {
        [DataType(DataType.Password)]
        [Display(Name = "AdminChallenge_YourPassword")]
        public string? Password { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        HSUser? administrator = await _users.GetUserAsync(User);

        if (administrator is null)
        {
            return NotFound();
        }

        // Already elevated - a bookmark or a back button rather than a request to prove again.
        if (_elevation.IsElevated(HttpContext, administrator.Id))
        {
            return Redirect(Destination());
        }

        await LoadAsync(administrator);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        HSUser? administrator = await _users.GetUserAsync(User);

        if (administrator is null)
        {
            return NotFound();
        }

        StepUpResult proof = await _stepUp.ProveAsync(HttpContext, administrator, Input.Password);

        if (!proof.Succeeded)
        {
            StatusMessage = _stepUpText.Describe(proof);
            await LoadAsync(administrator);

            return Page();
        }

        _elevation.Grant(HttpContext, administrator.Id);

        return Redirect(Destination());
    }

    /// <summary>
    /// Sends a password-less administrator to their provider, coming back to
    /// <see cref="OnGetReauthenticatedAsync"/> here - so the proof is earned on this page and spent
    /// on it.
    /// </summary>
    public async Task<IActionResult> OnPostReauthenticateAsync(string provider)
    {
        HSUser? administrator = await _users.GetUserAsync(User);

        if (administrator is null)
        {
            return NotFound();
        }

        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

        string? redirectUrl = Url.Page("/Admin/Challenge", pageHandler: "Reauthenticated", values: new { returnUrl = ReturnUrl });
        AuthenticationProperties? challenge = await _stepUp.ProviderChallengeAsync(administrator, provider, redirectUrl!);

        return challenge is null ? NotFound() : new ChallengeResult(provider, challenge);
    }

    /// <summary>The provider's answer, which the confirm button below then spends.</summary>
    public async Task<IActionResult> OnGetReauthenticatedAsync()
    {
        HSUser? administrator = await _users.GetUserAsync(User);

        if (administrator is null)
        {
            return NotFound();
        }

        StatusMessage = _stepUpText.Describe(await _stepUp.RecordProviderProofAsync(HttpContext, administrator));

        return RedirectToPage(new { returnUrl = ReturnUrl });
    }

    /// <summary>
    /// Where to go once elevated: where they were headed, if that is somewhere in the administration
    /// screens, and the roster otherwise.
    /// </summary>
    /// <remarks>
    /// <b>Both halves of the test matter.</b> <c>IsLocalUrl</c> keeps the page from becoming an open
    /// redirect; the <c>/Admin</c> prefix keeps an elevation earned for these screens from delivering
    /// the browser anywhere else, which is the same argument the cookie's own path scoping makes.
    /// </remarks>
    private string Destination()
    {
        if (ReturnUrl is not null
            && Url.IsLocalUrl(ReturnUrl)
            && ReturnUrl.StartsWith(AdminElevation.Path + "/", StringComparison.OrdinalIgnoreCase))
        {
            return ReturnUrl;
        }

        return Url.Page("/Admin/Users/Index") ?? "/";
    }

    private async Task LoadAsync(HSUser administrator)
    {
        UsesPassword = await _stepUp.UsesPasswordAsync(administrator);
        Providers = UsesPassword ? [] : await _externalSignIn.ProvidersAsync();
    }
}
