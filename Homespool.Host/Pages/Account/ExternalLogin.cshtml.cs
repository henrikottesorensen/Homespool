// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;

using Duende.IdentityModel;

using Homespool.Host.Localisation;
using Homespool.Host.Services;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

using SignInResult = Microsoft.AspNetCore.Identity.SignInResult;

namespace Homespool.Host.Pages.Account;

[AllowAnonymous]
public class ExternalLoginModel : PageModel
{
    private readonly SignInManager<HSUser> _signInManager;
    private readonly UserManager<HSUser> _userManager;
    private readonly IUserStore<HSUser> _userStore;
    private readonly IUserEmailStore<HSUser> _emailStore;
    private readonly IEmailSender _emailSender;
    private readonly ILogger<ExternalLoginModel> _logger;
    private readonly AccountConfirmationPolicy _accountConfirmationPolicy;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public ExternalLoginModel(SignInManager<HSUser> signInManager,
                              UserManager<HSUser> userManager,
                              IUserStore<HSUser> userStore,
                              ILogger<ExternalLoginModel> logger,
                              IEmailSender emailSender,
                              AccountConfirmationPolicy accountConfirmationPolicy,
                              IStringLocalizer<SharedResource> localiser)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _userStore = userStore;
        _emailStore = GetEmailStore();
        _logger = logger;
        _emailSender = emailSender;
        _accountConfirmationPolicy = accountConfirmationPolicy;
        _localiser = localiser;
    }

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
    public string ProviderDisplayName { get; set; }

    /// <summary>
    ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public string ReturnUrl { get; set; }

    /// <summary>
    ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    [TempData]
    public string ErrorMessage { get; set; }

    /// <summary>
    ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public class InputModel
    {
        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        [Required]
        [EmailAddress]
        public string Email { get; set; }

        /// <summary>
        /// The sign-in name, asked for here for the same reason <c>Setup</c> and <c>Register</c> ask:
        /// an account's username is chosen, and an address is not a username.
        /// </summary>
        [Required]
        [StringLength(HSUser.UsernameMaxLength)]
        [Display(Name = "Account_Username")]
        public string Username { get; set; }
    }

    public IActionResult OnGet()
    {
        return RedirectToPage("./Login");
    }

    /// <summary>
    /// Begins sign-in with an external provider, if the named one is actually registered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The name is checked because it arrives from the form.</b> Unchecked, it went straight to
    /// <see cref="ChallengeResult"/>, and a scheme nobody registered throws
    /// <see cref="InvalidOperationException"/> - so an anonymous POST of any string answered 500. No
    /// provider is registered today, so the login page renders no buttons and the only caller is a
    /// hand-made request; what it cost was an endpoint that faults on demand and fills the log.
    /// </para>
    /// <para>
    /// <b>A name that <i>is</i> a scheme but not an external one was the stranger case.</b> Posting
    /// <c>PrusaConnect</c> challenged the printer authentication handler and got its 401 - harmless,
    /// and a sign-in page reaching into the printer protocol is not a thing to leave reachable.
    /// Checking against <c>GetExternalAuthenticationSchemesAsync</c> rather than "is a scheme at all"
    /// is what separates the two.
    /// </para>
    /// <para>
    /// <b>400 rather than a message.</b> Nothing a person can do in the UI reaches this - there are no
    /// buttons to press - so there is no human to write prose for, and inventing a localised string
    /// for a hand-made request would be pretending otherwise. Once a provider is registered the check
    /// passes and this path is unchanged.
    /// </para>
    /// <para>
    /// The scaffold stays. External identity providers are scoped out rather than rejected
    /// (<c>notes/phase-1.5-enrollment.md</c>, "External OIDC scoped out"), and that note carries the
    /// trap for whoever adds one: inbound claim mapping must be turned off on the new handler, or the
    /// short JWT names this codebase uses stop matching.
    /// </para>
    /// </remarks>
    public async Task<IActionResult> OnPostAsync(string provider, string returnUrl = null)
    {
        IEnumerable<AuthenticationScheme> external = await _signInManager.GetExternalAuthenticationSchemesAsync();

        if (!external.Any(scheme => string.Equals(scheme.Name, provider, StringComparison.Ordinal)))
        {
            return BadRequest();
        }

        // Request a redirect to the external login provider.
        string redirectUrl = Url.Page("./ExternalLogin", pageHandler: "Callback", values: new { returnUrl });
        AuthenticationProperties properties = _signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
        return new ChallengeResult(provider, properties);
    }

    public async Task<IActionResult> OnGetCallbackAsync(string returnUrl = null, string remoteError = null)
    {
        returnUrl = returnUrl ?? Url.Content("~/");
        if (remoteError != null)
        {
            ErrorMessage = _localiser["Account_ExternalProviderError", remoteError];
            return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
        }

        ExternalLoginInfo info = await _signInManager.GetExternalLoginInfoAsync();
        if (info == null)
        {
            ErrorMessage = _localiser["Account_ExternalLoginError"];
            return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
        }

        // Sign in the user with this external login provider if the user already has a login.
        SignInResult result =
            await _signInManager.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: false,
                                                          bypassTwoFactor: true);
        if (result.Succeeded)
        {
            _logger.LogInformation("{Name} logged in with {LoginProvider} provider.", info.Principal.Identity.Name,
                                   info.LoginProvider);
            return LocalRedirect(returnUrl);
        }

        if (result.IsLockedOut)
        {
            return RedirectToPage("./Lockout");
        }
        else
        {
            // If the user does not have an account, then ask the user to create an account.
            ReturnUrl = returnUrl;
            ProviderDisplayName = info.ProviderDisplayName;
            if (info.Principal.HasClaim(c => c.Type == JwtClaimTypes.Email))
            {
                Input = new InputModel
                {
                    Email = info.Principal.FindFirstValue(JwtClaimTypes.Email),
                };
            }

            return Page();
        }
    }

    public async Task<IActionResult> OnPostConfirmationAsync(string returnUrl = null)
    {
        returnUrl = returnUrl ?? Url.Content("~/");

        // Get the information about the user from the external login provider
        ExternalLoginInfo info = await _signInManager.GetExternalLoginInfoAsync();
        if (info == null)
        {
            ErrorMessage = _localiser["Account_ExternalLoginConfirmError"];
            return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
        }

        if (ModelState.IsValid)
        {
            HSUser user = CreateUser();

            await _userStore.SetUserNameAsync(user, Input.Username, CancellationToken.None);
            await _emailStore.SetEmailAsync(user, Input.Email, CancellationToken.None);

            _accountConfirmationPolicy.Apply(user);

            IdentityResult result = await _userManager.CreateAsync(user);
            if (result.Succeeded)
            {
                result = await _userManager.AddLoginAsync(user, info);
                if (result.Succeeded)
                {
                    _logger.LogInformation("User created an account using {Name} provider.", info.LoginProvider);

                    string userId = await _userManager.GetUserIdAsync(user);
                    string code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
                    code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
                    string callbackUrl = Url.Page(
                        "/Account/ConfirmEmail",
                        pageHandler: null,
                        values: new { userId = userId, code = code },
                        protocol: Request.Scheme);

                    // The request's culture, and correct: the person registering is the person who
                    // reads this. There is also nothing stored to consult - the account was created
                    // a few lines above with no language chosen.
                    EmailSendResult sendResult = await _emailSender.SendEmailAsync(
                        Input.Email,
                        _localiser["Email_ConfirmSubject"],
                        _localiser["Email_ConfirmBody", HtmlEncoder.Default.Encode(callbackUrl)]);

                    // Own address, just supplied by the external provider - safe to report. See RegisterModel.
                    bool emailFailed = sendResult == EmailSendResult.Failed;

                    // If account confirmation is required, we need to show the link if we don't have a real email sender
                    if (_userManager.Options.SignIn.RequireConfirmedAccount)
                    {
                        return RedirectToPage("./RegisterConfirmation",
                                              new { Email = Input.Email, emailFailed = emailFailed });
                    }

                    await _signInManager.SignInAsync(user, isPersistent: false, info.LoginProvider);
                    return LocalRedirect(returnUrl);
                }
            }

            foreach (IdentityError error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }
        }

        ProviderDisplayName = info.ProviderDisplayName;
        ReturnUrl = returnUrl;
        return Page();
    }

    private HSUser CreateUser()
    {
        try
        {
            return Activator.CreateInstance<HSUser>();
        }
        catch
        {
            throw new InvalidOperationException($"Can't create an instance of '{nameof(HSUser)}'. " +
                                                $"Ensure that '{nameof(HSUser)}' is not an abstract class and has a parameterless constructor, or alternatively " +
                                                $"override the external login page in /Areas/Identity/Pages/Account/ExternalLogin.cshtml");
        }
    }

    private IUserEmailStore<HSUser> GetEmailStore()
    {
        if (!_userManager.SupportsUserEmail)
        {
            throw new NotSupportedException("The default UI requires a user store with email support.");
        }

        return (IUserEmailStore<HSUser>)_userStore;
    }
}
