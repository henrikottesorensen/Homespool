// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.Services;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Homespool.Host.Pages.Account;

/// <summary>
/// Invite-accept page. Registration is invite-only (phase-1.5 §15 step 6): this page is reachable
/// only with a valid, unexpired, unused invite token, and creates the account bound to the invite's
/// email. It replaces the public self-service registration the Identity scaffold shipped with.
/// </summary>
public class RegisterModel : PageModel
{
    private readonly SignInManager<HSUser> _signInManager;
    private readonly UserManager<HSUser> _userManager;
    private readonly IUserStore<HSUser> _userStore;
    private readonly IUserEmailStore<HSUser> _emailStore;
    private readonly ILogger<RegisterModel> _logger;
    private readonly IEmailSender _emailSender;
    private readonly AccountConfirmationPolicy _accountConfirmationPolicy;
    private readonly InvitationService _invitationService;
    private readonly TeamService _teamService;
    private readonly UnitOfWork _unitOfWork;

    public RegisterModel(UserManager<HSUser> userManager,
                         IUserStore<HSUser> userStore,
                         SignInManager<HSUser> signInManager,
                         ILogger<RegisterModel> logger,
                         IEmailSender emailSender,
                         AccountConfirmationPolicy accountConfirmationPolicy,
                         InvitationService invitationService,
                         TeamService teamService,
                         UnitOfWork unitOfWork)
    {
        _userManager = userManager;
        _userStore = userStore;
        _emailStore = GetEmailStore();
        _signInManager = signInManager;
        _logger = logger;
        _emailSender = emailSender;
        _accountConfirmationPolicy = accountConfirmationPolicy;
        _invitationService = invitationService;
        _teamService = teamService;
        _unitOfWork = unitOfWork;
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

    public class InputModel
    {
        /// <summary>
        /// The sign-in name, and what the interface calls this person.
        /// </summary>
        /// <remarks>
        /// The one thing on this form the invitee chooses about their identity - the address is the
        /// invite's and is never taken from what they typed (§15 dec. 3). Only the length is checked
        /// here; the character set and uniqueness belong to Identity's <c>UserValidator</c>.
        /// </remarks>
        [Required]
        [StringLength(HSUser.UsernameMaxLength)]
        [Display(Name = "Username")]
        public string Username { get; set; }

        [Required]
        [StringLength(100, ErrorMessage = "Validation_Length", MinimumLength = 6)]
        [DataType(DataType.Password)]
        [Display(Name = "Password")]
        public string Password { get; set; }

        [DataType(DataType.Password)]
        [Display(Name = "Confirm password")]
        [Compare(nameof(Password), ErrorMessage = "Validation_PasswordMismatch")]
        public string ConfirmPassword { get; set; }
    }

    public async Task OnGetAsync(string returnUrl, CancellationToken cancellationToken)
    {
        ReturnUrl = returnUrl;

        Invitation invitation = await _invitationService.ValidateAsync(InviteId, DecodeToken(Code), cancellationToken);

        InviteValid = invitation is not null;
        Email = invitation?.Email;
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

        if (!ModelState.IsValid)
        {
            return Page();
        }

        HSUser user = new();

        // One transaction wraps the account, its team(s) and spending the invite - mirroring Setup.
        // Any early return before CommitAsync disposes the transaction uncommitted, rolling back every
        // write made through it, so no failure path needs a compensating delete.
        await using IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            // The address is bound to the invite, never anything the invitee typed (§15 dec. 3). The
            // username is theirs to pick: it is not an identity the invite confers, and it cannot be
            // used to reach anything the invite did not already grant.
            await _userStore.SetUserNameAsync(user, Input.Username, cancellationToken);
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
                await _teamService.AddMemberAsync(teamId, user.Id, canRead: true, canUse: true, canManage: false,
                                                  cancellationToken);
            }

            await _invitationService.MarkUsedAsync(invitation, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to accept invitation {InviteId}; rolling back the account.", InviteId);
            ModelState.AddModelError(string.Empty, "Could not complete registration. Please try again.");

            return Page();
        }

        _logger.LogInformation("Invitation {InviteId} accepted; account created for {Email}.", InviteId, invitation.Email);

        // Follow AccountConfirmationPolicy (§15 decision 3): when SMTP is configured the account is
        // unconfirmed, so send the confirmation mail and hold at RegisterConfirmation; otherwise it is
        // already confirmed and we can sign straight in.
        if (!user.EmailConfirmed)
        {
            string userId = await _userManager.GetUserIdAsync(user);
            string confirmToken = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            confirmToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(confirmToken));
            string callbackUrl = Url.Page(
                "/Account/ConfirmEmail",
                pageHandler: null,
                values: new { userId, code = confirmToken, returnUrl },
                protocol: Request.Scheme);

            EmailSendResult sendResult = await _emailSender.SendEmailAsync(invitation.Email, "Confirm your email",
                                                                           $"Please confirm your account by <a href='{HtmlEncoder.Default.Encode(callbackUrl)}'>clicking here</a>.");

            bool emailFailed = sendResult == EmailSendResult.Failed;

            return RedirectToPage("RegisterConfirmation", new { email = invitation.Email, returnUrl, emailFailed });
        }

        await _signInManager.SignInAsync(user, isPersistent: false);

        return LocalRedirect(returnUrl);
    }

    /// <summary>Reverses the Base64Url encoding the accept link uses. Null/invalid input yields null.</summary>
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
