using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
using Homespool.Host.Pages.Printers;
using Homespool.Host.RateLimiting;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Account;

/// <summary>
/// Where a signed-in person proves they hold the account before a page that wants more than a
/// session will act: any one of the ways this account signs in - its password, a passkey, or a fresh
/// round trip to its identity provider - proved once, and good for <see cref="RecentProof.Window"/>
/// of work on the pages that asked.
/// </summary>
/// <remarks>
/// <para>
/// <b>One page, offering exactly what the account holds.</b> A step-up that knew only the password
/// excluded the strongest credential an account can have and turned away an account that had none;
/// one that lived on each page that wanted it drifted, and was forgotten on the next. So the pages
/// declare <see cref="RequireRecentProofAttribute"/> and send people here, and this page asks the
/// account which credentials it has rather than assuming.
/// </para>
/// <para>
/// <b>Every proof is against the signed-in account, never against whoever a credential names.</b> The
/// password scheme takes the session's account by contract; the passkey scheme names whoever's key
/// signed, so the answer is compared with the session before it counts - a bound challenge tells the
/// browser which keys to offer, and is not a check; the provider round trip is keyed to the account
/// and its answer checked against the logins it holds.
/// </para>
/// <para>
/// <b>A wrong password backs off the account's step-ups, never its sign-in</b>, as every step-up does,
/// so a session-holder guessing here cannot lock the owner out of taking the session back. A passkey
/// or a provider answer that does not count costs nothing and proves nothing.
/// </para>
/// <para>
/// <b>What a proof buys is <see cref="RecentProof"/>'s window, on its own timer.</b> A login starts a
/// session and does not open these pages; the mark earned here does, slides with use, and goes with the
/// session on sign-out. Afterwards the person is sent back to any local address they were sent from -
/// the page that wanted the proof - and to the account pages otherwise.
/// </para>
/// </remarks>
[Authorize]
[EnableRateLimiting(RateLimitPolicies.SignIn)]
[NoRecentProof("This is where a recent proof is earned; requiring one would redirect it to itself.")]
public class ReauthenticateModel : PageModel
{
    private readonly UserManager<HSUser> _users;
    private readonly RecentProof _proof;
    private readonly LocalSignInRules _rules;
    private readonly StepUpGate _stepUp;
    private readonly StepUpText _stepUpText;
    private readonly ExternalSignIn _externalSignIn;
    private readonly IOptionsMonitor<PasskeyAuthenticationOptions> _passkeys;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ILogger<ReauthenticateModel> _logger;

    public ReauthenticateModel(UserManager<HSUser> users,
                               RecentProof proof,
                               LocalSignInRules rules,
                               StepUpGate stepUp,
                               StepUpText stepUpText,
                               ExternalSignIn externalSignIn,
                               IOptionsMonitor<PasskeyAuthenticationOptions> passkeys,
                               IStringLocalizer<SharedResource> localiser,
                               ILogger<ReauthenticateModel> logger)
    {
        _users = users;
        _proof = proof;
        _rules = rules;
        _stepUp = stepUp;
        _stepUpText = stepUpText;
        _externalSignIn = externalSignIn;
        _passkeys = passkeys;
        _localiser = localiser;
        _logger = logger;
    }

    /// <summary>Where the person was heading when they were asked.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    /// <summary>Whether this account has a password to prove.</summary>
    public bool UsesPassword { get; private set; }

    /// <summary>Whether this account has a passkey, and the relying-party id covers the host it is on.</summary>
    public bool PasskeysAvailable { get; private set; }

    /// <summary>The identity providers this account signs in through.</summary>
    public IReadOnlyList<AuthenticationScheme> Providers { get; private set; } = [];

    public class InputModel
    {
        [DataType(DataType.Password)]
        [Display(Name = "Passkeys_PasswordLabel")]
        public string? Password { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        HSUser? user = await _users.GetUserAsync(User);

        if (user is null)
        {
            return NotFound();
        }

        // Already proved recently - a bookmark or a back button rather than a request to prove again.
        if (_proof.IsProved(HttpContext, user.Id))
        {
            return Redirect(Destination());
        }

        await LoadAsync(user);

        return Page();
    }

    /// <summary>The password, for an account that has one.</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        HSUser? user = await _users.GetUserAsync(User);

        if (user is null)
        {
            return NotFound();
        }

        // An account with no password proves itself another way; a password posted for one is not
        // compared with anything, and the page says which ways are open.
        if (!await _stepUp.UsesPasswordAsync(user))
        {
            return await RefusedAsync(user, _localiser["Reauthenticate_NoPassword"]);
        }

        StepUpResult proof = await _stepUp.PasswordAsync(HttpContext, Input.Password);

        if (!proof.Succeeded)
        {
            return await RefusedAsync(user, _stepUpText.Describe(proof));
        }

        return Proved(user, UserPasswordAuthenticationHandler.AuthenticationMethod);
    }

    /// <summary>
    /// The first half of a passkey proof: a challenge bound to the signed-in account, for the script to
    /// hand to the browser. A POST so the antiforgery token guards it; 404 where passkeys are withheld.
    /// </summary>
    public async Task<IActionResult> OnPostPasskeyOptionsAsync()
    {
        HSUser? user = await _users.GetUserAsync(User);

        if (user is null || !Scheme.Covers(Request.Host))
        {
            return NotFound();
        }

        AuthenticationProperties properties = new();
        properties.Items[PasskeyAuthenticationHandler.UserIdProperty] = user.Id.ToString(CultureInfo.InvariantCulture);

        return Challenge(properties, Schemes.Passkey);
    }

    /// <summary>
    /// The second half: the assertion, verified by the passkey scheme against the ceremony it started,
    /// and then against the session - the scheme names whoever's key signed, and only this account's
    /// counts.
    /// </summary>
    public async Task<IActionResult> OnPostPasskeyAsync(string? credential)
    {
        HSUser? user = await _users.GetUserAsync(User);

        if (user is null)
        {
            return NotFound();
        }

        AuthenticateResult assertion = await HttpContext.AuthenticateWithAsync(Schemes.Passkey, new PasskeyCredential(credential));

        if (!assertion.Succeeded)
        {
            if (assertion.Refusal() == SignInRefusal.LockedOut)
            {
                return await RefusedAsync(user, _localiser["StepUp_LockedOut", BackoffWait.Format(_localiser, await _rules.RemainingLockoutAsync(user))]);
            }

            _logger.LogInformation("Re-authentication refused for user {UserId}: the passkey assertion was refused.", user.Id);

            return await RefusedAsync(user, _localiser["Reauthenticate_PasskeyRefused"]);
        }

        string? proved = assertion.Principal.FindFirstValue(JwtClaimTypes.Subject);

        if (!string.Equals(proved, user.Id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            _logger.LogWarning("Re-authentication refused for user {UserId}: the passkey belongs to user {PasskeyUserId}.", user.Id, proved);

            return await RefusedAsync(user, _localiser["Reauthenticate_PasskeyRefused"]);
        }

        return Proved(user, PasskeyAuthenticationHandler.AuthenticationMethod);
    }

    /// <summary>
    /// Sends an account that signs in through <paramref name="provider"/> to re-authenticate there,
    /// coming back to <see cref="OnGetProviderReturnedAsync"/>.
    /// </summary>
    public async Task<IActionResult> OnPostProviderAsync(string? provider)
    {
        HSUser? user = await _users.GetUserAsync(User);

        if (user is null || string.IsNullOrEmpty(provider))
        {
            return NotFound();
        }

        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

        string redirectUrl = Url.Page("/Account/Reauthenticate", pageHandler: "ProviderReturned", values: new { returnUrl = ReturnUrl })!;
        AuthenticationProperties? challenge = await _stepUp.ProviderChallengeAsync(user, provider, redirectUrl);

        return challenge is null ? NotFound() : new ChallengeResult(provider, challenge);
    }

    /// <summary>
    /// The provider's answer. When it counts, the proof is the act, so the session is re-issued here
    /// rather than kept for a later click.
    /// </summary>
    public async Task<IActionResult> OnGetProviderReturnedAsync()
    {
        HSUser? user = await _users.GetUserAsync(User);

        if (user is null)
        {
            return NotFound();
        }

        ProviderProofOutcome outcome = await _stepUp.VerifyProviderProofAsync(HttpContext, user);

        if (outcome.Refusal is not null)
        {
            StatusMessage = _stepUpText.Describe(outcome);

            return RedirectToPage(new { returnUrl = ReturnUrl });
        }

        return Proved(user, RecentProof.ProviderMethod);
    }

    private PasskeyAuthenticationOptions Scheme => _passkeys.Get(Schemes.Passkey);

    /// <summary>
    /// Where to go once proved: where they were headed, if that is somewhere on this site, and the
    /// account pages otherwise. <c>IsLocalUrl</c> is what keeps this from being an open redirect.
    /// </summary>
    private string Destination()
    {
        return ReturnUrl is not null && Url.IsLocalUrl(ReturnUrl)
            ? ReturnUrl
            : Url.Page("/Account/Manage/Index") ?? "/";
    }

    private IActionResult Proved(HSUser user, string method)
    {
        _proof.Grant(HttpContext, user.Id, method);

        _logger.LogInformation("User {UserId} proved themselves by {AuthenticationMethod}.", user.Id, method);

        return Redirect(Destination());
    }

    private async Task<IActionResult> RefusedAsync(HSUser user, string message)
    {
        StatusMessage = message;
        await LoadAsync(user);

        return Page();
    }

    private async Task LoadAsync(HSUser user)
    {
        UsesPassword = await _stepUp.UsesPasswordAsync(user);
        PasskeysAvailable = Scheme.Covers(Request.Host) && (await _users.GetPasskeysAsync(user)).Count > 0;

        IList<UserLoginInfo> logins = await _users.GetLoginsAsync(user);

        Providers = logins.Count == 0
            ? []
            : (await _externalSignIn.ProvidersAsync())
              .Where(scheme => logins.Any(login => string.Equals(login.LoginProvider, scheme.Name, StringComparison.Ordinal)))
              .ToList();
    }
}
