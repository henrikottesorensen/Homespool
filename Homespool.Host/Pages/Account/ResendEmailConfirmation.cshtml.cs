// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.Accounts;
using Homespool.Host.Localisation;
using Homespool.Host.Mail;
using Homespool.Model;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;

namespace Homespool.Host.Pages.Account;

[AllowAnonymous]
public class ResendEmailConfirmationModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly IEmailSender _emailSender;
    private readonly AttemptLimiter _attemptLimiter;
    private readonly TimeProvider _timeProvider;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public ResendEmailConfirmationModel(
        UserManager<HSUser> userManager,
        IEmailSender emailSender,
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
    public class InputModel
    {
        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        [Required]
        [EmailAddress]
        public string Email { get; set; }
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

        HSUser user = await _userManager.FindByEmailAsync(Input.Email);
        if (user == null)
        {
            ModelState.AddModelError(string.Empty, _localiser["Account_VerificationSent"]);
            return Page();
        }

        // Bounded per target account for the reason ForgotPassword's send is: this form is anonymous
        // and mails whatever registered address is typed into it, so the account the mail lands on is
        // the only thing that can be counted. A backed-off account gets the same sentence and no
        // mail, so the refusal does not become the existence answer the null arm above withholds.
        // Confirming the address clears the count - see ConfirmEmailModel.
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (await _attemptLimiter.RemainingLockoutAsync(
                user.Id, LimitedAction.SendConfirmationEmail, now, cancellationToken) is not null)
        {
            ModelState.AddModelError(string.Empty, _localiser["Account_VerificationSent"]);
            return Page();
        }

        await _attemptLimiter.RecordFailedAttemptAsync(
            user.Id, LimitedAction.SendConfirmationEmail, now, cancellationToken);

        string userId = await _userManager.GetUserIdAsync(user);
        string code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
        code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
        string callbackUrl = Url.Page(
            "/Account/ConfirmEmail",
            pageHandler: null,
            values: new { userId = userId, code = code },
            protocol: Request.Scheme);

        // The account's language, not the request's: this page is anonymous, so the browser asking
        // may not belong to the person who reads what it sends.
        (string subject, string body) = UserCultures.InCulture(user.Language, () => (
            _localiser["Email_ConfirmSubject"].Value,
            _localiser["Email_ConfirmBody", HtmlEncoder.Default.Encode(callbackUrl)].Value));

        // Result deliberately discarded, for the same reason as ForgotPassword: this is only reached when the
        // account exists, so reporting a send failure would confirm its existence.
        _ = await _emailSender.SendEmailAsync(Input.Email, subject, body);

        ModelState.AddModelError(string.Empty, _localiser["Account_VerificationSent"]);
        return Page();
    }
}
