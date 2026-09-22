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
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;

using Homespool.Host.Accounts;
using Homespool.Host.Localisation;
using Homespool.Host.Mail;
using Homespool.Host.RateLimiting;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.SignIn)]
public class ResendEmailConfirmationModel : PageModel
{
    /// <summary>How long an account waits after one confirmation mail before this form will send it another.</summary>
    /// <remarks>
    /// <see cref="ForgotPasswordModel.SendCooldown"/>'s figure for its reason: a confirmation link
    /// outlives the cooldown many times over and a later one does not cancel it, so a refused send
    /// leaves a working link in the inbox.
    /// </remarks>
    public static readonly TimeSpan SendCooldown = ForgotPasswordModel.SendCooldown;

    private readonly UserManager<HSUser> _userManager;
    private readonly IDeferredEmailSender _emailSender;
    private readonly AttemptLimiter _attemptLimiter;
    private readonly TimeProvider _timeProvider;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public ResendEmailConfirmationModel(
        UserManager<HSUser> userManager,
        IDeferredEmailSender emailSender,
        AttemptLimiter attemptLimiter,
        TimeProvider timeProvider,
        IStringLocalizer<SharedResource> localiser)
    {
        _userManager = userManager;
        _emailSender = emailSender;
        _attemptLimiter = attemptLimiter;
        _timeProvider = timeProvider;
        _localiser = localiser;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;
    }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        HSUser? user = await _userManager.FindByEmailAsync(Input.Email);

        // The second test is this page's own precondition, and it was missing: a confirmed address
        // has nothing left to confirm, so mailing one is a link that changes nothing - and it is what
        // let this form mail every registered address rather than only the unconfirmed ones, which is
        // the population it exists for. Silently, and before anything is counted, for the reason the
        // null arm is silent: a refusal that looked different would say the address is registered,
        // and grinding at a confirmed address should cost nothing and produce nothing.
        if (user == null || await _userManager.IsEmailConfirmedAsync(user))
        {
            ModelState.AddModelError(string.Empty, Answer());
            return Page();
        }

        // Held to a cooldown per target account, as ForgotPassword's send is and for its reasons: this
        // form is anonymous and mails whatever registered address is typed into it, so the account the
        // mail lands on is the only thing that can be bounded, and a wait that grew would be grown by
        // a stranger. An account inside its cooldown gets the same sentence and no mail, so the refusal
        // does not become the existence answer the null arm above withholds.
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (await _attemptLimiter.RemainingLockoutAsync(
                user.Id, LimitedAction.SendConfirmationEmail, now, cancellationToken) is not null)
        {
            ModelState.AddModelError(string.Empty, Answer());
            return Page();
        }

        await _attemptLimiter.StartCooldownAsync(
            user.Id, LimitedAction.SendConfirmationEmail, now, SendCooldown, cancellationToken);

        string code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
        code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
        string callbackUrl = EmailedToken.Link(Url, "/Account/ConfirmEmail", new { userUuid = user.Uuid, code = code });

        // The account's language, not the request's: this page is anonymous, so the browser asking
        // may not belong to the person who reads what it sends.
        (string subject, string body) = UserCultures.InCulture(user.Language, () => (
            _localiser["Email_ConfirmSubject"].Value,
            _localiser["Email_ConfirmBody", HtmlEncoder.Default.Encode(callbackUrl)].Value));

        // Queued rather than sent here, for the same two reasons as ForgotPassword: this is only
        // reached when the account exists and is unconfirmed, so reporting a send failure would
        // confirm as much - and so would waiting for the send, which took long enough to time.
        // To the stored address rather than the typed one, for the reason ForgotPassword gives: the
        // lookup folds look-alike spellings onto this account, and the link belongs to its owner.
        _emailSender.Enqueue(IdentityConfiguration.EmailOf(user), subject, body);

        ModelState.AddModelError(string.Empty, Answer());
        return Page();
    }

    /// <summary>
    /// The one sentence every post is answered with, whether it mailed, was inside the cooldown, or
    /// named an address with nothing to confirm - worded so that it is true of all three.
    /// </summary>
    private string Answer()
    {
        return _localiser["Account_VerificationSentOrRecent", (int)SendCooldown.TotalMinutes];
    }
}
