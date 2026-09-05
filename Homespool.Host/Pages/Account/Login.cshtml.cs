// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Pages.Account;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly SignInManager<HSUser> _signInManager;
    private readonly UserManager<HSUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ILogger<LoginModel> _logger;
    private readonly IPasswordHasher<HSUser> _passwordHasher;
    private readonly IOptionsMonitor<PasskeyAuthenticationOptions> _passkeys;

    public LoginModel(SignInManager<HSUser> signInManager,
                      UserManager<HSUser> userManager,
                      ILogger<LoginModel> logger,
                      IStringLocalizer<SharedResource> localiser,
                      IPasswordHasher<HSUser> passwordHasher,
                      IOptionsMonitor<PasskeyAuthenticationOptions> passkeys)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _localiser = localiser;
        _logger = logger;
        _passwordHasher = passwordHasher;
        _passkeys = passkeys;
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

        ExternalLogins = (await _signInManager.GetExternalAuthenticationSchemesAsync()).ToList();
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
    /// <b>The checks the password path runs still run</b>, in the order <c>PasswordSignInAsync</c>
    /// runs them: lockout first, then whether the account may sign in at all, which is where the
    /// confirmed-account rule lives. A refused assertion gets the wrong-password message, so the
    /// form is no more of an oracle for passkeys than it is for passwords.
    /// </para>
    /// </remarks>
    public async Task<IActionResult> OnPostPasskeyAsync(bool rememberMe = false, string returnUrl = null)
    {
        returnUrl ??= Url.Content("~/");

        ExternalLogins = (await _signInManager.GetExternalAuthenticationSchemesAsync()).ToList();
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

        AuthenticateResult assertion = await HttpContext.AuthenticateAsync(Schemes.Passkey);

        if (!assertion.Succeeded)
        {
            ModelState.AddModelError(string.Empty, _localiser["Account_InvalidLogin"]);

            return Page();
        }

        HSUser user = await _userManager.GetUserAsync(assertion.Principal);

        if (user is null)
        {
            _logger.LogWarning("A verified passkey assertion resolved to no account.");
            ModelState.AddModelError(string.Empty, _localiser["Account_InvalidLogin"]);

            return Page();
        }

        if (await _userManager.IsLockedOutAsync(user))
        {
            _logger.LogWarning("User account locked out.");

            return RedirectToPage("./Lockout");
        }

        if (!await _signInManager.CanSignInAsync(user))
        {
            ModelState.AddModelError(string.Empty, _localiser["Account_InvalidLogin"]);

            return Page();
        }

        await _userManager.ResetAccessFailedCountAsync(user);
        await _signInManager.SignInAsync(user, rememberMe, PasskeyAuthenticationHandler.AuthenticationMethod);

        _logger.LogInformation("User logged in with a passkey.");

        return LocalRedirect(returnUrl);
    }

    public async Task<IActionResult> OnPostAsync(string returnUrl = null)
    {
        returnUrl ??= Url.Content("~/");

        ExternalLogins = (await _signInManager.GetExternalAuthenticationSchemesAsync()).ToList();
        PasskeysAvailable = _passkeys.Get(Schemes.Passkey).Covers(Request.Host);

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
            // username may not contain '@' (UsernameValidator) - so this order settles
            // nothing that could be ambiguous; it is just the cheaper lookup first.
            HSUser user = await _userManager.FindByNameAsync(Input.Login)
                          ?? await _userManager.FindByEmailAsync(Input.Login);

            if (user is null)
            {
                // Verified against a decoy before answering, so this branch costs what the branch
                // below costs. The same message and the same page were already deliberate - telling
                // an anonymous caller which addresses and usernames exist is the enumeration this
                // form is exposed enough to care about - but sameness that stops at the wording
                // leaves the timing to answer the question instead: a miss returned after two
                // indexed lookups where a hit runs a full PBKDF2 verification first.
                PasswordVerificationDecoy.Verify(_passwordHasher, Input.Password);

                ModelState.AddModelError(string.Empty, _localiser["Account_InvalidLogin"]);

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

            ModelState.AddModelError(string.Empty, _localiser["Account_InvalidLogin"]);

            return Page();
        }

        // Something failed, redisplay form.
        return Page();
    }
}
