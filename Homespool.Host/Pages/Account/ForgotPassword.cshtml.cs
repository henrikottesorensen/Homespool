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

[AllowAnonymous] // Nobody asking for a password reset can be signed in.
[EnableRateLimiting(RateLimitPolicies.SignIn)]
public class ForgotPasswordModel : PageModel
{
    /// <summary>How long an account waits after one reset mail before this form will send it another.</summary>
    /// <remarks>
    /// Far shorter than a reset link lives, and a later link does not cancel an earlier one - so
    /// whenever a send is refused, a link that still works is already in the inbox. The confirmation
    /// page names this figure, which is what lets it be true for every caller.
    /// </remarks>
    public static readonly TimeSpan SendCooldown = TimeSpan.FromMinutes(15);

    private readonly UserManager<HSUser> _userManager;
    private readonly IDeferredEmailSender _emailSender;
    private readonly AttemptLimiter _attemptLimiter;
    private readonly TimeProvider _timeProvider;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public ForgotPasswordModel(
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

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (ModelState.IsValid)
        {
            HSUser? user = await _userManager.FindByEmailAsync(Input.Email);

            // The third test is what makes "an external account has no local password" a rule rather
            // than a preference. ResetPasswordAsync does not care whether a password already exists -
            // it writes the hash either way - so without this, an account created through a provider
            // could give itself one by asking for a reset, and the decision would hold everywhere
            // except the one door that is open to anybody who knows the address.
            //
            // Silently, and that matters: this arm already exists so as not to reveal whether an
            // address is registered, and a refusal that looked different here would answer the same
            // question the arm was written to refuse. No mail is sent, and the caller sees exactly
            // what an unknown address sees. See ChangePasswordModel.HasPassword for the other half.
            //
            // A closed account is answered the same way. The manager refuses its token when it is
            // redeemed, so a link would change nothing; not sending one keeps a mail that cannot work
            // out of the owner's inbox.
            if (user == null ||
                user.DeactivatedAt is not null ||
                !(await _userManager.IsEmailConfirmedAsync(user)) ||
                !(await _userManager.HasPasswordAsync(user)))
            {
                // Don't reveal that the user does not exist, is closed, is not confirmed, or signs in elsewhere
                return RedirectToPage("./ForgotPasswordConfirmation");
            }

            // Each send starts a fixed cooldown on the account the mail is addressed to, and an account
            // inside it is answered with the same redirect and no mail. The address is the only handle
            // an anonymous caller offers, so the target account - not the caller - is the thing that
            // can be bounded; without this, anyone who knows an address can fill its inbox and drain
            // the deployment's SMTP quota at request rate. Silently, for the reason the arm above is
            // silent: a refusal that looked different here would say the address is registered.
            //
            // A cooldown rather than a counted backoff, because the caller is not the account: a wait
            // that grew with use would be grown by whoever knows the address and served by the person
            // who needs the mail. This one never grows and a refused request does not restart it, so
            // the most a stranger can do is make the owner use a link under SendCooldown old.
            DateTimeOffset now = _timeProvider.GetUtcNow();

            if (await _attemptLimiter.RemainingLockoutAsync(
                    user.Id, LimitedAction.SendPasswordResetEmail, now, cancellationToken) is not null)
            {
                return RedirectToPage("./ForgotPasswordConfirmation");
            }

            await _attemptLimiter.StartCooldownAsync(
                user.Id, LimitedAction.SendPasswordResetEmail, now, SendCooldown, cancellationToken);

            // For more information on how to enable account confirmation and password reset please
            // visit https://go.microsoft.com/fwlink/?LinkID=532713
            string code = await _userManager.GeneratePasswordResetTokenAsync(user);
            code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
            string callbackUrl = EmailedToken.Link(Url, "/Account/ResetPassword", new { code });

            // Written in the account's language rather than the request's. Nobody has to be signed in
            // to ask for a reset, so the browser here belongs to whoever typed the address - which may
            // not be the person who reads the email, and is exactly the case HSUser.Language exists
            // for. Null means they never chose, and the deployment default stands.
            (string subject, string body) = UserCultures.InCulture(user.Language, () => (
                _localiser["Email_ResetPasswordSubject"].Value,
                _localiser["Email_ResetPasswordBody", HtmlEncoder.Default.Encode(callbackUrl)].Value));

            // Queued rather than sent here, and both halves of that matter. The result is discarded
            // because the send is only attempted when the account exists and is confirmed - see the
            // early return above - so surfacing a failure would distinguish "account exists, mail
            // broke" from "no such account", which is exactly what that early return is written to
            // hide; the failure is in the log and in the startup SMTP probe instead. And the wait
            // goes with it: awaiting a whole SMTP conversation here made the answer for a registered
            // address measurably slower than the one an unknown address gets, which says the same
            // thing to anyone willing to time two requests.
            //
            // To the stored address, never the typed one. The lookup matched on the normalised key,
            // which folds more than case: NFC maps the kelvin sign to K and the uppercasing maps a
            // long s to S, so a look-alike spelling finds this account - and mailing that spelling
            // would hand the reset token to whoever holds the look-alike mailbox.
            _emailSender.Enqueue(IdentityConfiguration.EmailOf(user), subject, body);

            return RedirectToPage("./ForgotPasswordConfirmation");
        }

        return Page();
    }
}
