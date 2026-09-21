// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;

using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
using Homespool.Host.Mail;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Account.Manage;

/// <summary>
/// The account's address, and changing it - behind a recent proof that the person at the keyboard
/// holds the account, not just a live session.
/// </summary>
/// <remarks>
/// <para>
/// <b>The address is where a forgotten password is sent, so moving it is the last step of a
/// takeover.</b> The confirmation link goes to the new address alone, which is the attacker's if they
/// chose it, and the old address hears only once the change has landed; a session somebody else got
/// hold of must therefore not be able to start the change. The change handler alone carries
/// <see cref="RequireRecentProofAttribute"/>, since reading the address and re-sending its verification
/// want nothing more than a session; the view offers the way to prove before the button, and the filter
/// refuses the post without it.
/// </para>
/// <para>
/// <b>Each send holds its own button off for <see cref="SendCooldown"/>.</b> The proof decides who may
/// send and this decides how often: the change link goes to whatever address is typed, so without it
/// a proved session could point the deployment's mail at a third party at request rate.
/// A flat wait rather than the counted backoff the anonymous mail forms use: the caller is the owner,
/// and a mistyped address should cost a minute, not an hour.
/// </para>
/// </remarks>
[Authorize]
public class EmailModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly IEmailSender _emailSender;
    private readonly RecentProof _proof;
    private readonly AttemptLimiter _attemptLimiter;
    private readonly TimeProvider _timeProvider;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public EmailModel(UserManager<HSUser> userManager,
                      IEmailSender emailSender,
                      RecentProof proof,
                      AttemptLimiter attemptLimiter,
                      TimeProvider timeProvider,
                      IStringLocalizer<SharedResource> localiser)
    {
        _userManager = userManager;
        _emailSender = emailSender;
        _proof = proof;
        _attemptLimiter = attemptLimiter;
        _timeProvider = timeProvider;
        _localiser = localiser;
    }

    /// <summary>How long a send holds its button off before another may go.</summary>
    public static readonly TimeSpan SendCooldown = TimeSpan.FromMinutes(1);

    /// <summary>Whether the person has proved themselves recently, so the change button is worth showing.</summary>
    public bool Proved { get; private set; }

    public string? Email { get; set; }

    public bool IsEmailConfirmed { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required]
        [EmailAddress]
        [StorableEmailAddress]
        [Display(Name = "Manage_NewEmail")]
        public string NewEmail { get; set; } = string.Empty;
    }

    private async Task LoadAsync(HSUser user)
    {
        // RequireUniqueEmail makes the user validator refuse a blank address on create and update, so
        // every stored account has one.
        string email = (await _userManager.GetEmailAsync(user))!;
        Email = email;

        Input = new InputModel
        {
            NewEmail = email,
        };

        IsEmailConfirmed = await _userManager.IsEmailConfirmedAsync(user);
        Proved = _proof.IsProved(HttpContext, user.Id);
    }

    public async Task<IActionResult> OnGetAsync()
    {
        HSUser? user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        await LoadAsync(user);
        return Page();
    }

    [RequireRecentProof]
    public async Task<IActionResult> OnPostChangeEmailAsync(CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(user);
            return Page();
        }

        string? email = await _userManager.GetEmailAsync(user);
        if (Input.NewEmail != email)
        {
            if (!await TryStartCooldownAsync(user.Id, LimitedAction.ChangeEmail, cancellationToken))
            {
                StatusMessage = _localiser["Manage_EmailSendCooldown"];
                return RedirectToPage();
            }

            string code = await _userManager.GenerateChangeEmailTokenAsync(user, Input.NewEmail);
            code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));

            // Names a page of this application, so the route always resolves.
            string callbackUrl = Url.Page(
                "/Account/ConfirmEmailChange",
                pageHandler: null,
                values: new { userUuid = user.Uuid, email = Input.NewEmail, code = code },
                protocol: Request.Scheme)!;

            // The account's language, like every mail to an account. The culture provider usually
            // resolved the same one for this request, but only when a language is stored - and the
            // rule is the page's to keep, not the middleware's. None stored leaves this browser's.
            (string subject, string body) = UserCultures.InCulture(user.Language, () => (
                _localiser["Email_ConfirmSubject"].Value,
                _localiser["Email_ConfirmBody", HtmlEncoder.Default.Encode(callbackUrl)].Value));

            EmailSendResult sendResult = await _emailSender.SendEmailAsync(Input.NewEmail, subject, body);

            StatusMessage = sendResult == EmailSendResult.Failed ?
                _localiser["Manage_EmailChangeSendFailed"] :
                _localiser["Manage_EmailChangeSent"];
            return RedirectToPage();
        }

        StatusMessage = _localiser["Manage_EmailUnchanged"];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSendVerificationEmailAsync(CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(user);
            return Page();
        }

        if (!await TryStartCooldownAsync(user.Id, LimitedAction.SendVerificationEmail, cancellationToken))
        {
            StatusMessage = _localiser["Manage_EmailSendCooldown"];
            return RedirectToPage();
        }

        // Never blank: RequireUniqueEmail, as LoadAsync says.
        string email = (await _userManager.GetEmailAsync(user))!;
        string code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
        code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));

        // Names a page of this application, so the route always resolves.
        string callbackUrl = Url.Page(
            "/Account/ConfirmEmail",
            pageHandler: null,
            values: new { userUuid = user.Uuid, code = code },
            protocol: Request.Scheme)!;

        // The account's language, for the reason given on the change-address send above.
        (string subject, string body) = UserCultures.InCulture(user.Language, () => (
            _localiser["Email_ConfirmSubject"].Value,
            _localiser["Email_ConfirmBody", HtmlEncoder.Default.Encode(callbackUrl)].Value));

        EmailSendResult sendResult = await _emailSender.SendEmailAsync(email, subject, body);

        StatusMessage = sendResult == EmailSendResult.Failed ?
            _localiser["Manage_VerificationSendFailed"] :
            _localiser["Account_VerificationSent"];
        return RedirectToPage();
    }

    /// <summary>
    /// Starts <paramref name="action"/>'s cooldown and answers true, or answers false without touching
    /// it when the last send's is still running.
    /// </summary>
    /// <remarks>
    /// Started before the mail goes rather than after it lands, so a burst of posts cannot all pass the
    /// check while the first is still talking to the relay - and a failed send still spends the minute.
    /// </remarks>
    private async Task<bool> TryStartCooldownAsync(long userId, LimitedAction action, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (await _attemptLimiter.RemainingLockoutAsync(userId, action, now, cancellationToken) is not null)
        {
            return false;
        }

        await _attemptLimiter.StartCooldownAsync(userId, action, now, SendCooldown, cancellationToken);

        return true;
    }
}
