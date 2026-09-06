// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting(PasskeyChallengeRateLimit.PolicyName)]
public class LoginModel : PageModel
{
    /// <summary>The handler that issues a passkey challenge; the one handler on this page the rate limit applies to.</summary>
    public const string PasskeyOptionsHandler = "PasskeyOptions";

    private readonly ExternalSignIn _externalSignIn;
    private readonly UserManager<HSUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ILogger<LoginModel> _logger;
    private readonly IOptionsMonitor<PasskeyAuthenticationOptions> _passkeys;
    private readonly LocalSignInRules _rules;
    private readonly LocalSignIn _signIn;

    public LoginModel(ExternalSignIn externalSignIn,
                      UserManager<HSUser> userManager,
                      ILogger<LoginModel> logger,
                      IStringLocalizer<SharedResource> localiser,
                      IOptionsMonitor<PasskeyAuthenticationOptions> passkeys,
                      LocalSignInRules rules,
                      LocalSignIn signIn)
    {
        _externalSignIn = externalSignIn;
        _userManager = userManager;
        _localiser = localiser;
        _logger = logger;
        _passkeys = passkeys;
        _rules = rules;
        _signIn = signIn;
    }

    [BindProperty]
    public InputModel Input { get; set; }

    public IList<AuthenticationScheme> ExternalLogins { get; set; }

    /// <summary>
    /// Whether the passkey button is offered on this request: a relying-party id is configured and it
    /// covers the host the page was asked for.
    /// </summary>
    /// <remarks>
    /// The configured name compared against the request's host, not either alone. The deployment
    /// answers to more than one name - the public hostname, a LAN alias, a bare address - and the
    /// browser runs the ceremony only when the name in its address bar is the relying-party id or a
    /// subdomain of it. So which name this person arrived by decides whether the button can work,
    /// and only the request knows that. A ceremony started from an uncovered host fails in the
    /// browser with nothing to say why; withholding the button is where that refusal gets a reason.
    /// </remarks>
    public bool PasskeysAvailable { get; set; }

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
        [Display(Name = "Account_EmailOrUsername")]
        public string Login { get; set; }

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; }

        [Display(Name = "Account_RememberMe")]
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

        ExternalLogins = (await _externalSignIn.ProvidersAsync()).ToList();
        PasskeysAvailable = _passkeys.Get(Schemes.Passkey).Covers(Request.Host);

        ReturnUrl = returnUrl;
    }

    /// <summary>
    /// The first half of a passkey sign-in: the script on the page asks for a challenge, and the
    /// passkey scheme answers with the request options and starts a ceremony.
    /// </summary>
    /// <remarks>
    /// A POST rather than a GET so that the antiforgery token guards it, which is what stops another
    /// site starting ceremonies against this one. 404 when passkeys are withheld here, and the script
    /// hides the button on that answer.
    /// </remarks>
    public IActionResult OnPostPasskeyOptions()
    {
        // The scheme answers 404 here too; bailing out first keeps a ceremony from being asked for
        // at all on a host it cannot be completed from.
        if (!_passkeys.Get(Schemes.Passkey).Covers(Request.Host))
        {
            return NotFound();
        }

        return Challenge(Schemes.Passkey);
    }

    /// <summary>
    /// The second half: the script posts the assertion, the passkey scheme verifies it against the
    /// ceremony it started, and a verified assertion is a complete sign-in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A verified passkey is the whole sign-in.</b> User verification is required of every
    /// assertion, so the credential proves possession and a face, a finger or a device passcode
    /// together, which is what a password plus an authenticator code proves the long way round.
    /// Nothing here consults the account's two-factor setting or the deployment's floor; the
    /// enrolment gate still asks a floor-on deployment's accounts to hold an authenticator app, and
    /// that is a rule about the account rather than about this sign-in.
    /// </para>
    /// <para>
    /// <b>The checks the password path runs still run</b>, in the scheme, the moment the assertion
    /// has named the account - nobody knows who is signing in before that - and before anything is
    /// stored or minted: whether the account is locked out, and whether it may sign in at all, which
    /// is where the confirmed-account rule lives. This page routes on the refusal as it does for a
    /// password. Any other refused assertion gets the wrong-password message, so the form is no more
    /// of an oracle for passkeys than it is for passwords.
    /// </para>
    /// </remarks>
    public async Task<IActionResult> OnPostPasskeyAsync(string credential = null, bool rememberMe = false, string returnUrl = null)
    {
        returnUrl ??= Url.Content("~/");

        ExternalLogins = (await _externalSignIn.ProvidersAsync()).ToList();
        PasskeysAvailable = _passkeys.Get(Schemes.Passkey).Covers(Request.Host);
        ReturnUrl = returnUrl;

        // The password form's fields are bound on every post to this page and are empty on this
        // one, so their "required" errors are dropped before this handler says anything of its own.
        ModelState.Clear();

        // The scheme refuses an assertion from an uncovered host itself, and is the check that
        // counts; this one saves running the ceremony to reach the same answer.
        if (!PasskeysAvailable)
        {
            ModelState.AddModelError(string.Empty, _localiser["Account_InvalidLogin"]);

            return Page();
        }

        AuthenticateResult assertion = await HttpContext.AuthenticateWithAsync(Schemes.Passkey, new PasskeyCredential(credential));

        if (!assertion.Succeeded)
        {
            if (assertion.Refusal() == SignInRefusal.LockedOut)
            {
                _logger.LogWarning("User account locked out.");

                return RedirectToPage("./Lockout");
            }

            ModelState.AddModelError(string.Empty, _localiser["Account_InvalidLogin"]);

            return Page();
        }

        await _signIn.SignInAsync(HttpContext, assertion.Principal, rememberMe);

        _logger.LogInformation("User logged in with a passkey.");

        return LocalRedirect(returnUrl);
    }

    public async Task<IActionResult> OnPostAsync(string returnUrl = null)
    {
        returnUrl ??= Url.Content("~/");

        ExternalLogins = (await _externalSignIn.ProvidersAsync()).ToList();
        PasskeysAvailable = _passkeys.Get(Schemes.Passkey).Covers(Request.Host);

        if (!ModelState.IsValid)
        {
            return Page();
        }

        // The password scheme does the checking - lookup by either identifier, the decoy verification
        // for an identifier nobody holds, the pre-sign-in check, and a wrong password counted toward
        // the lockout. Counting matters because this form has no rate limiting of its own and people
        // do expose self-hosted printer servers to the internet whatever the advice says; Identity's
        // defaults apply, five failures then five minutes, and the accepted cost is that someone who
        // knows an account's address can keep it locked out for those five minutes.
        AuthenticateResult password = await HttpContext.AuthenticateWithAsync(Schemes.UserPassword,
                                                                              new UserPasswordCredential(Input.Login, Input.Password));

        if (!password.Succeeded)
        {
            if (password.Refusal() == SignInRefusal.LockedOut)
            {
                _logger.LogWarning("User account locked out.");

                return RedirectToPage("./Lockout");
            }

            // One message for a wrong password, an unknown identifier and an account that may not
            // sign in: telling an anonymous caller which of those it was is the enumeration this form
            // is exposed enough to care about.
            ModelState.AddModelError(string.Empty, _localiser["Account_InvalidLogin"]);

            return Page();
        }

        HSUser user = await _userManager.GetUserAsync(password.Principal);

        if (await _signIn.OwesSecondFactorAsync(HttpContext, user))
        {
            await _signIn.BeginSecondFactorAsync(HttpContext, user);

            return RedirectToPage("./LoginWith2fa", new { ReturnUrl = returnUrl, Input.RememberMe });
        }

        await _signIn.SignInAsync(HttpContext, password.Principal, Input.RememberMe);

        _logger.LogInformation("User logged in.");

        return LocalRedirect(returnUrl);
    }
}
