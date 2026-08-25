// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;

using Duende.IdentityModel;

using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SignInResult = Microsoft.AspNetCore.Identity.SignInResult;

namespace Homespool.Host.Pages.Account;

[AllowAnonymous]
public class ExternalLoginModel : PageModel
{
    /// <summary>
    /// Authentication-property keys carrying an invite through the provider round trip. They ride in
    /// the same protected properties as <c>XsrfId</c>, so the provider never sees them and the
    /// callback cannot be handed a different invite than the one the challenge began with.
    /// </summary>
    private const string InviteIdKey = "homespool.invite_id";

    /// <summary><see cref="InviteIdKey"/>'s companion, the Base64Url token from the accept link.</summary>
    private const string InviteTokenKey = "homespool.invite_token";

    private readonly SignInManager<HSUser> _signInManager;
    private readonly UserManager<HSUser> _userManager;
    private readonly IUserStore<HSUser> _userStore;
    private readonly IUserEmailStore<HSUser> _emailStore;
    private readonly IEmailSender _emailSender;
    private readonly ILogger<ExternalLoginModel> _logger;
    private readonly AccountConfirmationPolicy _accountConfirmationPolicy;
    private readonly InvitationService _invitationService;
    private readonly TeamService _teamService;
    private readonly UnitOfWork _unitOfWork;
    private readonly OidcOptions _oidc;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public ExternalLoginModel(SignInManager<HSUser> signInManager,
                              UserManager<HSUser> userManager,
                              IUserStore<HSUser> userStore,
                              ILogger<ExternalLoginModel> logger,
                              IEmailSender emailSender,
                              AccountConfirmationPolicy accountConfirmationPolicy,
                              InvitationService invitationService,
                              TeamService teamService,
                              UnitOfWork unitOfWork,
                              IOptions<OidcOptions> oidc,
                              IStringLocalizer<SharedResource> localiser)
    {
        ArgumentNullException.ThrowIfNull(oidc);

        _signInManager = signInManager;
        _userManager = userManager;
        _userStore = userStore;
        _emailStore = GetEmailStore();
        _logger = logger;
        _emailSender = emailSender;
        _accountConfirmationPolicy = accountConfirmationPolicy;
        _invitationService = invitationService;
        _teamService = teamService;
        _unitOfWork = unitOfWork;
        _oidc = oidc.Value;
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
        /// The sign-in name, asked for here for the same reason <c>Setup</c> and <c>Register</c> ask:
        /// an account's username is chosen, and an address is not a username.
        /// </summary>
        /// <remarks>
        /// <b>The address is not on this form</b>, and was until the invite gate landed. It is the
        /// invite's, exactly as on <c>Register</c> (phase-1.5 §15 decision 3) - an address the caller
        /// could type would let a verified sign-in create an account bound to somebody else's.
        /// </remarks>
        [Required]
        [StringLength(HSUser.UsernameMaxLength)]
        [Display(Name = "Account_Username")]
        public string Username { get; set; }
    }

    /// <summary>
    /// The invite's bound address, shown read-only. The account is created as this, never as anything
    /// the caller supplied.
    /// </summary>
    public string Email { get; private set; }

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
    /// <see cref="InvalidOperationException"/> - so an anonymous POST of any string answered 500. What
    /// it cost was an endpoint that faults on demand and fills the log. The login page now renders a
    /// button per registered provider, so the ordinary caller is a form; the check is still what keeps
    /// a hand-made one from choosing its own scheme name.
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
    public async Task<IActionResult> OnPostAsync(string provider, string returnUrl = null, int? inviteId = null,
                                                 string code = null)
    {
        IEnumerable<AuthenticationScheme> external = await _signInManager.GetExternalAuthenticationSchemesAsync();

        if (!external.Any(scheme => string.Equals(scheme.Name, provider, StringComparison.Ordinal)))
        {
            return BadRequest();
        }

        // Request a redirect to the external login provider.
        string redirectUrl = Url.Page("./ExternalLogin", pageHandler: "Callback", values: new { returnUrl });
        AuthenticationProperties properties = _signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);

        // An invite presented here rides through the provider and back, so the callback can spend it
        // without trusting anything the provider says about who this is. Not validated yet - the round
        // trip takes time, and a check now would be a check on stale state; the callback does it.
        if (inviteId is int id && !string.IsNullOrEmpty(code))
        {
            properties.Items[InviteIdKey] = id.ToString(CultureInfo.InvariantCulture);
            properties.Items[InviteTokenKey] = code;
        }

        return new ChallengeResult(provider, properties);
    }

    public async Task<IActionResult> OnGetCallbackAsync(CancellationToken cancellationToken, string returnUrl = null,
                                                        string remoteError = null)
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
                                                          bypassTwoFactor: false);
        if (result.Succeeded)
        {
            _logger.LogInformation("{Name} logged in with {LoginProvider} provider.", info.Principal.Identity.Name,
                                   info.LoginProvider);
            return LocalRedirect(returnUrl);
        }

        // A second factor this deployment asked for is still asked for (Henrik, 2026-08-22). The
        // scaffold passed bypassTwoFactor: true, on the reasoning that the provider has just
        // authenticated somebody - but the provider's assurance is not the one the account holder
        // opted into, and an account that turns on an authenticator here is saying "not without this",
        // not "not without this unless another party vouches for me". Whether the provider did its own
        // multi-factor is unknowable from here: nothing in the callback can tell an amr from a wish.
        //
        // Without this arm the fall-through is worse than wrong, it is confusing: a RequiresTwoFactor
        // result is neither Succeeded nor IsLockedOut, so an account with an authenticator would drop
        // into the invite gate below and be told there is no invitation for it.
        if (result.RequiresTwoFactor)
        {
            return RedirectToPage("./LoginWith2fa", new { ReturnUrl = returnUrl, RememberMe = false });
        }

        if (result.IsLockedOut)
        {
            return RedirectToPage("./Lockout");
        }

        // No account is linked, so this is a creation - and registration is invite-only. Whether the
        // provider authenticated somebody is not the question; whether an invite says they may have an
        // account here is.
        Invitation invitation = await ResolveInviteAsync(info, cancellationToken);

        if (invitation is null)
        {
            _logger.LogInformation(
                "External sign-in with {LoginProvider} matched no account and carried no usable invite; refused.",
                info.LoginProvider);

            ErrorMessage = _localiser["Account_ExternalNoInvite"];

            return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
        }

        ReturnUrl = returnUrl;
        ProviderDisplayName = info.ProviderDisplayName;
        Email = invitation.Email;

        return Page();
    }

    /// <summary>
    /// The invite authorising this sign-in to create an account, or <c>null</c> if there is not one.
    /// </summary>
    /// <param name="info">The provider's principal and the properties the challenge carried.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <remarks>
    /// <para>
    /// <b>Two doors, and they are not equally strong.</b> The first is an invite token carried through
    /// the round trip from the accept link: possession of the token is the proof, exactly as on
    /// <c>Register</c>, and the provider's claims are not consulted at all. The second matches an
    /// outstanding invite by the address the provider asserts, which replaces that proof with the
    /// provider's word - so it is off unless an operator turns it on, and it refuses unless the
    /// provider positively says the address is verified. A missing <c>email_verified</c> is a refusal,
    /// never a pass: a provider that does not make the claim has not made the assurance.
    /// </para>
    /// <para>
    /// Both doors return a <i>tracked</i> invite, so the caller can spend it inside the same
    /// transaction as the account it authorises.
    /// </para>
    /// </remarks>
    private async Task<Invitation> ResolveInviteAsync(ExternalLoginInfo info, CancellationToken cancellationToken)
    {
        IDictionary<string, string> items = info.AuthenticationProperties?.Items;

        if (items is not null
            && items.TryGetValue(InviteIdKey, out string idText)
            && items.TryGetValue(InviteTokenKey, out string token)
            && int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int inviteId))
        {
            return await _invitationService.ValidateAsync(inviteId, DecodeToken(token), cancellationToken);
        }

        if (!_oidc.AllowInviteMatchByEmail || !ProviderVerifiedTheAddress(info.Principal))
        {
            return null;
        }

        return await _invitationService.FindOutstandingForEmailAsync(
            info.Principal.FindFirstValue(JwtClaimTypes.Email),
            cancellationToken);
    }

    /// <summary>
    /// Whether the provider asserts it verified the address it supplied. Anything other than an
    /// explicit true - absent, false, unparseable, or an address that is not there at all - is false.
    /// </summary>
    private static bool ProviderVerifiedTheAddress(ClaimsPrincipal principal)
    {
        if (string.IsNullOrWhiteSpace(principal.FindFirstValue(JwtClaimTypes.Email)))
        {
            return false;
        }

        string verified = principal.FindFirstValue(JwtClaimTypes.EmailVerified);

        return string.Equals(verified, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reverses the Base64Url encoding the accept link uses. Null or invalid input yields null.</summary>
    private static string DecodeToken(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public async Task<IActionResult> OnPostConfirmationAsync(CancellationToken cancellationToken, string returnUrl = null)
    {
        returnUrl = returnUrl ?? Url.Content("~/");

        // Get the information about the user from the external login provider
        ExternalLoginInfo info = await _signInManager.GetExternalLoginInfoAsync();
        if (info == null)
        {
            ErrorMessage = _localiser["Account_ExternalLoginConfirmError"];
            return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
        }

        // Re-resolved on post rather than carried from the GET: the invite could have been spent or
        // have expired while this form sat on screen, and the page holds nothing that would say so.
        // Same reasoning as Register's re-validation, and the same consequence if it is skipped.
        Invitation invitation = await ResolveInviteAsync(info, cancellationToken);

        if (invitation is null)
        {
            ErrorMessage = _localiser["Account_ExternalNoInvite"];

            return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
        }

        ProviderDisplayName = info.ProviderDisplayName;
        ReturnUrl = returnUrl;
        Email = invitation.Email;

        if (!ModelState.IsValid)
        {
            return Page();
        }

        HSUser user = CreateUser();

        // One transaction wraps the account, its team(s), the provider link and spending the invite -
        // mirroring Register. An early return before CommitAsync disposes it uncommitted, so no
        // failure path leaves an account behind that has no login attached to it.
        await using IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        IdentityResult result;

        try
        {
            // The address is the invite's, never the provider's - they agree by construction on the
            // matched-by-address door, and on the token door the provider's address was never consulted.
            await _userStore.SetUserNameAsync(user, Input.Username, cancellationToken);
            await _emailStore.SetEmailAsync(user, invitation.Email, cancellationToken);

            _accountConfirmationPolicy.Apply(user);

            result = await _userManager.CreateAsync(user);

            if (!result.Succeeded)
            {
                AddErrors(result);

                return Page();
            }

            result = await _userManager.AddLoginAsync(user, info);

            if (!result.Succeeded)
            {
                AddErrors(result);

                return Page();
            }

            // Every account gets its own default team, so printer-claim identity resolution always has
            // one; a team-scoped invite additionally joins that team. Identical to Register, because
            // the invite means the same thing however it was presented.
            await _teamService.AddDefaultTeamAsync(user.Id, DateTimeOffset.UtcNow, cancellationToken);

            if (invitation.TeamId is int teamId)
            {
                await _teamService.AddMemberAsync(teamId, user.Id, CapabilityPresets.Operator, cancellationToken);
            }

            await _invitationService.MarkUsedAsync(invitation, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to create an account from {LoginProvider}; rolling back.", info.LoginProvider);
            ModelState.AddModelError(string.Empty, _localiser["Account_RegistrationFailed"]);

            return Page();
        }

        _logger.LogInformation("Invitation {InviteId} accepted through {LoginProvider}; account created for {Email}.",
                               invitation.Id, info.LoginProvider, invitation.Email);

        // AccountConfirmationPolicy decides this, exactly as on Register: with SMTP configured the
        // account is unconfirmed and holds at RegisterConfirmation. The provider having verified the
        // address does not shortcut it - that policy is one rule for every creation path by design, and
        // an external sign-in is not the place to make it two.
        if (!user.EmailConfirmed)
        {
            string userId = await _userManager.GetUserIdAsync(user);
            string code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
            string callbackUrl = Url.Page(
                "/Account/ConfirmEmail",
                pageHandler: null,
                values: new { userId = userId, code = code, returnUrl },
                protocol: Request.Scheme);

            // The request's culture, and correct: the person registering is the person who
            // reads this. There is also nothing stored to consult - the account was created
            // a few lines above with no language chosen.
            EmailSendResult sendResult = await _emailSender.SendEmailAsync(
                invitation.Email,
                _localiser["Email_ConfirmSubject"],
                _localiser["Email_ConfirmBody", HtmlEncoder.Default.Encode(callbackUrl)]);

            // Own address, bound to the invite they just spent - safe to report. See RegisterModel.
            bool emailFailed = sendResult == EmailSendResult.Failed;

            return RedirectToPage("./RegisterConfirmation",
                                  new { Email = invitation.Email, returnUrl, emailFailed });
        }

        await _signInManager.SignInAsync(user, isPersistent: false, info.LoginProvider);

        return LocalRedirect(returnUrl);
    }

    /// <summary>Surfaces Identity's own failures on the form rather than as a fault.</summary>
    private void AddErrors(IdentityResult result)
    {
        foreach (IdentityError error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, error.Description);
        }
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
