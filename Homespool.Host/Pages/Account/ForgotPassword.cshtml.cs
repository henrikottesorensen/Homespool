// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

using Homespool.Host.Localisation;
using Homespool.Host.Mail;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;

namespace Homespool.Host.Pages.Account;

[AllowAnonymous] // Nobody asking for a password reset can be signed in.
public class ForgotPasswordModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly IEmailSender _emailSender;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public ForgotPasswordModel(
        UserManager<HSUser> userManager,
        IEmailSender emailSender,
        IStringLocalizer<SharedResource> localiser)
    {
        _userManager = userManager;
        _emailSender = emailSender;
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

    public async Task<IActionResult> OnPostAsync()
    {
        if (ModelState.IsValid)
        {
            HSUser user = await _userManager.FindByEmailAsync(Input.Email);

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
            if (user == null
                || !(await _userManager.IsEmailConfirmedAsync(user))
                || !(await _userManager.HasPasswordAsync(user)))
            {
                // Don't reveal that the user does not exist, is not confirmed, or signs in elsewhere
                return RedirectToPage("./ForgotPasswordConfirmation");
            }

            // For more information on how to enable account confirmation and password reset please
            // visit https://go.microsoft.com/fwlink/?LinkID=532713
            string code = await _userManager.GeneratePasswordResetTokenAsync(user);
            code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
            string callbackUrl = Url.Page(
                "/Account/ResetPassword",
                pageHandler: null,
                values: new { code },
                protocol: Request.Scheme);

            // Written in the account's language rather than the request's. Nobody has to be signed in
            // to ask for a reset, so the browser here belongs to whoever typed the address - which may
            // not be the person who reads the email, and is exactly the case HSUser.Language exists
            // for. Null means they never chose, and the deployment default stands.
            (string subject, string body) = UserCultures.InCulture(user.Language, () => (
                _localiser["Email_ResetPasswordSubject"].Value,
                _localiser["Email_ResetPasswordBody", HtmlEncoder.Default.Encode(callbackUrl)].Value));

            // Result deliberately discarded. The send is only attempted when the account exists and is
            // confirmed - see the early return above - so surfacing a failure here would distinguish
            // "account exists, mail broke" from "no such account", which is exactly what that early return
            // is written to hide. The failure is in the log and in the startup SMTP probe instead.
            _ = await _emailSender.SendEmailAsync(Input.Email, subject, body);

            return RedirectToPage("./ForgotPasswordConfirmation");
        }

        return Page();
    }
}
