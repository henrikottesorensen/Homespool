// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.Authentication;
using Homespool.Host.Accounts;
using Homespool.Host.Localisation;
using Homespool.Host.Mail;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace Homespool.Host.Pages.Account;

/// <summary>
/// Invite-accept page. Registration is invite-only: this page is reachable
/// only with a valid, unexpired, unused invite token, and creates the account bound to the invite's
/// email. It replaces the public self-service registration the Identity scaffold shipped with.
/// </summary>
[AllowAnonymous] // The invite token is the credential here, not a session.
public class RegisterModel : PageModel
{
    private readonly LocalSignIn _signIn;
    private readonly LocalSignInRules _rules;
    private readonly ExternalSignIn _externalSignIn;
    private readonly UserManager<HSUser> _userManager;
    private readonly IUserStore<HSUser> _userStore;
    private readonly IUserEmailStore<HSUser> _emailStore;
    private readonly ILogger<RegisterModel> _logger;
    private readonly IEmailSender _emailSender;
    private readonly AccountConfirmationPolicy _accountConfirmationPolicy;
    private readonly InvitationService _invitationService;
    private readonly TeamService _teamService;
    private readonly UnitOfWork _unitOfWork;
    private readonly ApiTokenService _apiTokens;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public RegisterModel(UserManager<HSUser> userManager,
                         IUserStore<HSUser> userStore,
                         LocalSignIn signIn,
                         LocalSignInRules rules,
                         ExternalSignIn externalSignIn,
                         ILogger<RegisterModel> logger,
                         IEmailSender emailSender,
                         AccountConfirmationPolicy accountConfirmationPolicy,
                         InvitationService invitationService,
                         TeamService teamService,
                         UnitOfWork unitOfWork,
                         ApiTokenService apiTokens,
                         IStringLocalizer<SharedResource> localiser)
    {
        _userManager = userManager;
        _userStore = userStore;
        _emailStore = GetEmailStore();
        _signIn = signIn;
        _rules = rules;
        _externalSignIn = externalSignIn;
        _logger = logger;
        _emailSender = emailSender;
        _accountConfirmationPolicy = accountConfirmationPolicy;
        _localiser = localiser;
        _invitationService = invitationService;
        _teamService = teamService;
        _unitOfWork = unitOfWork;
        _apiTokens = apiTokens;
    }

    /// <summary>Invite id, carried in the accept link and echoed back on post via a hidden field.</summary>
    [BindProperty(SupportsGet = true)]
    public int InviteId { get; set; }

    /// <summary>The Base64Url-encoded invite token from the accept link.</summary>
    [BindProperty(SupportsGet = true)]
    public string Code { get; set; }

    [BindProperty]
    public InputModel Input { get; set; }

    public string ReturnUrl { get; set; }

    /// <summary>True when the invite validated; the view shows the password form only then.</summary>
    public bool InviteValid { get; private set; }

    /// <summary>The invite's bound email, shown read-only. The account is created as this address.</summary>
    public string Email { get; private set; }

    /// <summary>
    /// Registered external providers, so an invitee can accept with one <em>instead of</em> setting a
    /// password — arriving with the provider as their only credential.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the token door's entry point, and it is the stronger of the two.</b> The invite's id
    /// and token ride through the provider round trip, so the callback spends the invite on proof the
    /// invitee holds the emailed secret and never consults the provider's claims. The other door —
    /// matching an outstanding invite against a provider-asserted address — trusts
    /// <c>email_verified</c> instead, which is why it is off by default.
    /// </para>
    /// <para>
    /// <b>Without this button the safer door had no UI</b>, so an operator who wanted provider-only
    /// accounts had to switch the weaker one on to get them. That is the wrong way round, and it is
    /// the reason this exists (Henrik, 2026-08-22).
    /// </para>
    /// <para>
    /// <b>Not offered when reactivating.</b> That flow exists because a provider went away, and it
    /// removes the dead links; offering to accept with a provider there would be offering the thing
    /// that just failed.
    /// </para>
    /// </remarks>
    public IList<AuthenticationScheme> ExternalLogins { get; private set; } = [];

    /// <summary>
    /// True when an account already exists for this address and has no password, so redeeming the
    /// invite <b>reactivates</b> it rather than creating a second one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the recovery path for an account orphaned by its identity provider.</b> An account
    /// created through a provider has no password by rule, and if that provider goes away for good it
    /// cannot sign in, cannot reset, and there is no administrator-side reset. The answer is an
    /// invite — but until this branch existed, sending one did not work: <c>RequireUniqueEmail</c> is
    /// on and this page created unconditionally, so accepting failed on a duplicate address and the
    /// invite was unredeemable.
    /// </para>
    /// <para>
    /// <b>Only an account with no password is adoptable.</b> That bound is what keeps an invite from
    /// being a way to re-credential an account that already works, without knowing its password, which
    /// is an account takeover with an administrator's signature on it.
    /// </para>
    /// <para>
    /// <b>It is a wider set than the orphaned one, and nothing here narrows it further.</b> Holding no
    /// password is not the same as having no way in: an account whose provider still answers, and one
    /// that signs in with a passkey, both pass this test - and redeeming the invite then takes the
    /// provider links away, passkeys untouched, since they are not logins. What the flow rests on is
    /// possession of an invite for that address, not the account's own standing.
    /// </para>
    /// <para>
    /// <b>The proof is unchanged.</b> The invite is still single-use, still expiring, and still has to
    /// be presented with its token — this branch changes what redeeming does, not what it takes.
    /// </para>
    /// </remarks>
    public bool Reactivating { get; private set; }

    /// <summary>
    /// The existing account's username when <see cref="Reactivating"/> or <see cref="Recovering"/>.
    /// Shown read-only and never re-chosen: it is already theirs, and letting an invite rename an
    /// account is not a thing this flow is for.
    /// </summary>
    public string ExistingUsername { get; private set; }

    /// <summary>
    /// True when this invite is a <b>recovery</b> an administrator issued for an account whose owner
    /// lost their credentials: redeeming it sets a new password on that account rather than creating
    /// one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It names its account by id</b>, so nothing about the address decides who is recovered - an
    /// address change between issuing and redeeming cannot retarget it, and the page shows whose
    /// account it is before anything is typed.
    /// </para>
    /// <para>
    /// <b>The proof is the same as any invite's</b>: single-use, expiring, and presented with its
    /// token. What differs is what redeeming does, and that an administrator had to name the subject
    /// to issue it at all.
    /// </para>
    /// </remarks>
    public bool Recovering { get; private set; }

    /// <summary>Whether redeeming this recovery also clears the account's authenticator.</summary>
    public bool RecoveryClearsTwoFactor { get; private set; }

    public class InputModel
    {
        /// <summary>
        /// The sign-in name, and what the interface calls this person.
        /// </summary>
        /// <remarks>
        /// The one thing on this form the invitee chooses about their identity - the address is the
        /// invite's and is never taken from what they typed. Only the length is checked
        /// here; the character set and uniqueness belong to Identity's <c>UserValidator</c>.
        /// </remarks>
        [Required]
        [StringLength(HSUser.UsernameMaxLength)]
        [Display(Name = "Account_Username")]
        public string Username { get; set; }

        [Required]
        [StringLength(100, ErrorMessage = "Validation_Length", MinimumLength = IdentityConfiguration.MinimumPasswordLength)]
        [DataType(DataType.Password)]
        [Display(Name = "Account_Password")]
        public string Password { get; set; }

        [DataType(DataType.Password)]
        [Display(Name = "Account_ConfirmPassword")]
        [Compare(nameof(Password), ErrorMessage = "Validation_PasswordMismatch")]
        public string ConfirmPassword { get; set; }
    }

    public async Task OnGetAsync(string returnUrl, CancellationToken cancellationToken)
    {
        ReturnUrl = returnUrl;

        Invitation invitation = await _invitationService.ValidateAsync(InviteId, DecodeToken(Code), cancellationToken);

        InviteValid = invitation is not null;
        Email = invitation?.Email;

        if (invitation is not null)
        {
            await ResolveReactivationAsync(invitation);
        }

        ExternalLogins = [.. await _externalSignIn.ProvidersAsync()];
    }

    /// <summary>
    /// Decides whether this invite adopts an existing account, and returns that account when it does.
    /// </summary>
    private async Task<HSUser> ResolveReactivationAsync(Invitation invitation)
    {
        if (invitation.RecoversUserId is long recovered)
        {
            // Named, not looked up: the address on a recovery is where the link was sent, and the id
            // is who it is for. A miss means the account is gone, which is a recovery that can no
            // longer be redeemed rather than one to redirect at somebody else.
            HSUser subject = await _userManager.FindByIdAsync(recovered.ToString(CultureInfo.InvariantCulture));

            if (subject is null)
            {
                return null;
            }

            Recovering = true;
            RecoveryClearsTwoFactor = invitation.ClearsTwoFactor;
            ExistingUsername = subject.UserName;

            return subject;
        }

        HSUser existing = await _userManager.FindByEmailAsync(invitation.Email);

        if (existing is null)
        {
            return null;
        }

        Reactivating = !await _userManager.HasPasswordAsync(existing);
        ExistingUsername = Reactivating ? existing.UserName : null;

        return existing;
    }

    public async Task<IActionResult> OnPostAsync(string returnUrl, CancellationToken cancellationToken)
    {
        returnUrl ??= Url.Content("~/");
        ReturnUrl = returnUrl;

        // Re-validate on post: the token could be tampered with, and the invite could have expired or
        // been spent since the form was rendered.
        Invitation invitation = await _invitationService.ValidateAsync(InviteId, DecodeToken(Code), cancellationToken);

        if (invitation is null)
        {
            InviteValid = false;

            return Page();
        }

        InviteValid = true;
        Email = invitation.Email;

        HSUser existing = await ResolveReactivationAsync(invitation);

        if (Recovering)
        {
            if (existing is null)
            {
                // The account this recovery names is gone. Nothing to give back, and nothing to
                // create in its place: an invite that says "recover" is not an invite that says
                // "register".
                InviteValid = false;

                return Page();
            }

            ModelState.Remove($"{nameof(Input)}.{nameof(InputModel.Username)}");

            if (!ModelState.IsValid)
            {
                return Page();
            }

            return await RecoverAsync(existing, invitation, returnUrl, cancellationToken);
        }

        if (existing is not null && !Reactivating)
        {
            // The address already has an account that works. Refused rather than attempted, because
            // what CreateAsync answers here is a duplicate-address validation error, which reads as a
            // problem with the form rather than as "this invite is for somebody who can already sign
            // in" - and is what this page did before the branch below existed.
            ModelState.AddModelError(string.Empty, _localiser["Account_InviteAddressAlreadyActive"]);

            return Page();
        }

        if (Reactivating)
        {
            // The username is not on the reactivation form: it is the account's own, shown read-only.
            // Left in ModelState it fails [Required] on a field nobody was offered.
            ModelState.Remove($"{nameof(Input)}.{nameof(InputModel.Username)}");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (Reactivating)
        {
            return await ReactivateAsync(existing, invitation, returnUrl, cancellationToken);
        }

        HSUser user = new();

        // One transaction wraps the account, its team(s) and spending the invite - mirroring Setup.
        // Any early return before CommitAsync disposes the transaction uncommitted, rolling back every
        // write made through it, so no failure path needs a compensating delete.
        await using IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            // The address is bound to the invite, never anything the invitee typed. The
            // username is theirs to pick: it is not an identity the invite confers, and it cannot be
            // used to reach anything the invite did not already grant.
            await _userStore.SetUserNameAsync(user, Usernames.Prepare(Input.Username), cancellationToken);
            await _emailStore.SetEmailAsync(user, invitation.Email, cancellationToken);

            _accountConfirmationPolicy.Apply(user);

            IdentityResult createResult = await _userManager.CreateAsync(user, Input.Password);

            if (!createResult.Succeeded)
            {
                AddErrors(createResult);

                return Page();
            }

            // Every user gets their own default team, so printer-claim identity resolution (step 7)
            // always has one. A team-scoped invite additionally joins that existing team.
            await _teamService.AddDefaultTeamAsync(user.Id, DateTimeOffset.UtcNow, cancellationToken);

            if (invitation.TeamId is int teamId)
            {
                await _teamService.AddMemberAsync(teamId, user.Id, CapabilityPresets.Operator,
                                                  cancellationToken);
            }

            await _invitationService.MarkUsedAsync(invitation, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to accept invitation {InviteId}; rolling back the account.", InviteId);
            ModelState.AddModelError(string.Empty, _localiser["Account_RegistrationFailed"]);

            return Page();
        }

        _logger.LogInformation("Invitation {InviteId} accepted; account created for {Email}.", InviteId, invitation.Email);

        // Follow AccountConfirmationPolicy: when SMTP is configured the account is
        // unconfirmed, so send the confirmation mail and hold at RegisterConfirmation; otherwise it is
        // already confirmed and we can sign straight in.
        if (!user.EmailConfirmed)
        {
            return await HoldForConfirmationAsync(user, invitation.Email, returnUrl);
        }

        await _signIn.SignInAsync(HttpContext, user, isPersistent: false);

        return LocalRedirect(returnUrl);
    }

    /// <summary>
    /// Gives an account holding no password one, and takes its provider links away, in one step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The links go with it</b>, which mirrors what <c>Account/Manage/ExternalLogins</c> does in the
    /// other direction. For the account this flow exists for they point at a provider that is gone, and
    /// if that provider is ever rebuilt at the same address and reissues the same subject id, a stale
    /// row is a live credential nobody remembered granting. For an account whose provider still answers
    /// — which this flow does not distinguish — redeeming the invite is what ends that way in.
    /// </para>
    /// <para>
    /// <b>One transaction, because it is several round trips.</b> The half-done states are
    /// both wrong in the way this flow exists to prevent: a password added while the dead links remain
    /// is the parallel credential the rule refuses, and links removed while the password was rejected
    /// takes an account that had one way in and leaves it with none.
    /// </para>
    /// <para>
    /// <b>Nothing here touches teams or the confirmation flag.</b> The account already has its default
    /// team, so creating a second would be the bug; and an unconfirmed address stays unconfirmed,
    /// since redeeming an invite is not the same proof as answering mail sent to the address - the
    /// account is sent that mail and held at confirmation, as a new account is.
    /// </para>
    /// <para>
    /// <b>Then what follows a proved password.</b> The account existed before the invite, so it may be
    /// locked out, unconfirmed, or holding an authenticator, and the reactivation runs the same checks
    /// and the same second-factor step as the login page before anything becomes a session.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Gives an account back to its owner: a new password, optionally a cleared authenticator, and
    /// its API tokens revoked - on an invite an administrator issued naming that account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The password is reset, never added.</b> <c>ResetPasswordAsync</c> writes the hash whether or
    /// not one exists, so the one method serves an account that forgot its password and one that never
    /// had a local credential. Removing a password is what nothing here may do - one administrator
    /// always retaining one is what keeps a deployment from locking itself out.
    /// </para>
    /// <para>
    /// <b>An account with no password loses its provider logins</b>, exactly as reactivation does and
    /// for the same reason: a password beside a live provider link is the parallel credential the
    /// account rules refuse, so a recovery of a provider account is a swap rather than an addition.
    /// An account that already had a password keeps whatever it holds; the recovery is not a tidy-up.
    /// </para>
    /// <para>
    /// <b>The tokens go.</b> A recovery is somebody locked out of an account they may no longer have
    /// been alone in - the same reasoning that makes <c>Account/ResetPassword</c> revoke, and the
    /// opposite of a password change made from inside a live session.
    /// </para>
    /// <para>
    /// <b>The authenticator is cleared only when the invite says so.</b> Restoring a password gives
    /// the account back to somebody who can still prove possession of their device; clearing the
    /// second factor as well hands whoever holds the link the entire account, which is a thing an
    /// administrator decides deliberately when issuing.
    /// </para>
    /// <para>
    /// <b>One transaction, and the mail is outside it.</b> Every half-done state here is worse than
    /// either end - a cleared authenticator on an unchanged password, or a spent invite that changed
    /// nothing - and telling the owner is not part of the write.
    /// </para>
    /// </remarks>
    private async Task<IActionResult> RecoverAsync(HSUser subject, Invitation invitation, string returnUrl,
                                                   CancellationToken cancellationToken)
    {
        // A closed account is not recoverable, and saying so beats a redemption that appears to work
        // and then cannot sign in: the sign-in gate refuses it whatever credential it now holds.
        if (subject.DeactivatedAt is not null)
        {
            ModelState.AddModelError(string.Empty, _localiser["Account_RecoveryDeactivated"]);

            return Page();
        }

        bool hadPassword = await _userManager.HasPasswordAsync(subject);
        int revoked;

        await using IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            string resetToken = await _userManager.GeneratePasswordResetTokenAsync(subject);
            IdentityResult reset = await _userManager.ResetPasswordAsync(subject, resetToken, Input.Password);

            if (!reset.Succeeded)
            {
                AddErrors(reset);

                return Page();
            }

            if (!hadPassword)
            {
                foreach (UserLoginInfo login in await _userManager.GetLoginsAsync(subject))
                {
                    IdentityResult removed =
                        await _userManager.RemoveLoginAsync(subject, login.LoginProvider, login.ProviderKey);

                    if (!removed.Succeeded)
                    {
                        AddErrors(removed);

                        return Page();
                    }
                }
            }

            if (invitation.ClearsTwoFactor)
            {
                // Both halves, as Manage/ResetAuthenticator does: turning the flag off while the old
                // key still verifies leaves a second factor somebody can turn back on without ever
                // holding the device.
                IdentityResult disabled = await _userManager.SetTwoFactorEnabledAsync(subject, false);

                if (!disabled.Succeeded)
                {
                    AddErrors(disabled);

                    return Page();
                }

                await _userManager.ResetAuthenticatorKeyAsync(subject);
            }

            revoked = await _apiTokens.RevokeAllForUserAsync(subject.Id, cancellationToken);

            await _invitationService.MarkUsedAsync(invitation, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to redeem recovery invitation {InviteId}; rolling back.", InviteId);
            ModelState.AddModelError(string.Empty, _localiser["Account_RegistrationFailed"]);

            return Page();
        }

        _logger.LogWarning(
            "Recovery invitation {InviteId} redeemed for user {UserId}; two-factor cleared: {ClearedTwoFactor}; {RevokedTokenCount} API tokens revoked.",
            InviteId,
            subject.Id,
            invitation.ClearsTwoFactor,
            revoked);

        // The owner's only signal, if this recovery was not theirs. Sent to the account's own address
        // rather than the invite's, which is the same string today and need not stay so.
        await _emailSender.SendEmailAsync(
            subject.Email,
            _localiser["Email_RecoveredSubject"],
            _localiser["Email_RecoveredBody"]);

        // What follows a proved password anywhere else: the account may be locked out or unconfirmed,
        // and a recovery is not a way around either.
        switch (await _rules.PreSignInCheckAsync(subject))
        {
            case SignInRefusal.LockedOut:
                _logger.LogWarning("Recovered account {UserId} is locked out.", subject.Id);

                return RedirectToPage("./Lockout");

            case SignInRefusal.NotAllowed when !subject.EmailConfirmed:
                return await HoldForConfirmationAsync(subject, subject.Email, returnUrl);

            case SignInRefusal.NotAllowed:
                ModelState.AddModelError(string.Empty, _localiser["Account_InvalidLogin"]);

                return Page();
        }

        await _signIn.SignInAsync(HttpContext, subject, isPersistent: false);

        return LocalRedirect(returnUrl);
    }

    private async Task<IActionResult> ReactivateAsync(HSUser existing, Invitation invitation, string returnUrl,
                                                      CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            IdentityResult added = await _userManager.AddPasswordAsync(existing, Input.Password);

            if (!added.Succeeded)
            {
                AddErrors(added);

                return Page();
            }

            foreach (UserLoginInfo login in await _userManager.GetLoginsAsync(existing))
            {
                IdentityResult removed =
                    await _userManager.RemoveLoginAsync(existing, login.LoginProvider, login.ProviderKey);

                if (!removed.Succeeded)
                {
                    AddErrors(removed);

                    return Page();
                }
            }

            await _invitationService.MarkUsedAsync(invitation, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to reactivate the account for invitation {InviteId}; rolling back.", InviteId);
            ModelState.AddModelError(string.Empty, _localiser["Account_RegistrationFailed"]);

            return Page();
        }

        _logger.LogInformation("Invitation {InviteId} reactivated an existing account for {Email}.", InviteId,
                               invitation.Email);

        // The invite token and the new password are this path's proof; what follows is what follows
        // a proved password anywhere else. The account existed before this invite, so it may be locked
        // out, may still be unconfirmed, and may hold an authenticator - none of which a reactivation
        // is a way around.
        switch (await _rules.PreSignInCheckAsync(existing))
        {
            case SignInRefusal.LockedOut:
                _logger.LogWarning("Reactivated account {UserId} is locked out.", existing.Id);

                return RedirectToPage("./Lockout");

            case SignInRefusal.NotAllowed when !existing.EmailConfirmed:
                return await HoldForConfirmationAsync(existing, invitation.Email, returnUrl);

            case SignInRefusal.NotAllowed:
                ModelState.AddModelError(string.Empty, _localiser["Account_InvalidLogin"]);

                return Page();
        }

        if (await _signIn.OwesSecondFactorAsync(HttpContext, existing))
        {
            await _signIn.BeginSecondFactorAsync(HttpContext, existing);

            return RedirectToPage("./LoginWith2fa", new { ReturnUrl = returnUrl, RememberMe = false });
        }

        await _signIn.SignInAsync(HttpContext, existing, isPersistent: false);

        return LocalRedirect(returnUrl);
    }

    /// <summary>
    /// Sends <paramref name="user"/> the confirmation mail and holds at <c>RegisterConfirmation</c>:
    /// the account is not signed in until the address answers.
    /// </summary>
    private async Task<IActionResult> HoldForConfirmationAsync(HSUser user, string email, string returnUrl)
    {
        string userId = await _userManager.GetUserIdAsync(user);
        string confirmToken = await _userManager.GenerateEmailConfirmationTokenAsync(user);
        confirmToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(confirmToken));
        string callbackUrl = Url.Page(
            "/Account/ConfirmEmail",
            pageHandler: null,
            values: new { userId, code = confirmToken, returnUrl },
            protocol: Request.Scheme);

        // The request's culture, and correct: whoever accepted the invitation is whoever reads this.
        // The account exists by now but has chosen no language yet.
        EmailSendResult sendResult = await _emailSender.SendEmailAsync(
            email,
            _localiser["Email_ConfirmSubject"],
            _localiser["Email_ConfirmBody", HtmlEncoder.Default.Encode(callbackUrl)]);

        bool emailFailed = sendResult == EmailSendResult.Failed;

        return RedirectToPage("RegisterConfirmation", new { email, returnUrl, emailFailed });
    }

    /// <summary>Reverses the Base64Url encoding the accept link uses. Null/invalid input yields null.</summary>
    private static string DecodeToken(string code)
    {
        return EmailedToken.Decode(code);
    }

    private void AddErrors(IdentityResult result)
    {
        foreach (IdentityError error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, error.Description);
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
