// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
using Homespool.Host.Mail;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;

namespace Homespool.Host.Pages.Account.Manage;

/// <summary>
/// The account's address, and changing it - behind a recent proof that the person at the keyboard
/// holds the account, not just a live session.
/// </summary>
/// <remarks>
/// <b>The address is where a forgotten password is sent, so moving it is the last step of a
/// takeover.</b> The confirmation link goes to the new address alone, which is the attacker's if they
/// chose it, and the old address is told nothing; a session somebody else got hold of must therefore
/// not be able to start the change. The change handler alone carries <see cref="RequireRecentProofAttribute"/>,
/// since reading the address and re-sending its verification want nothing more than a session; the
/// view offers the way to prove before the button, and the filter refuses the post without it.
/// </remarks>
[Authorize]
public class EmailModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly IEmailSender _emailSender;
    private readonly RecentProof _proof;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public EmailModel(UserManager<HSUser> userManager,
                      IEmailSender emailSender,
                      RecentProof proof,
                      IStringLocalizer<SharedResource> localiser)
    {
        _userManager = userManager;
        _emailSender = emailSender;
        _proof = proof;
        _localiser = localiser;
    }

    /// <summary>Whether the person has proved themselves recently, so the change button is worth showing.</summary>
    public bool Proved { get; private set; }

    /// <summary>
    ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public string Email { get; set; }

    /// <summary>
    ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public bool IsEmailConfirmed { get; set; }

    /// <summary>
    ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    [TempData]
    public string StatusMessage { get; set; }

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
        [Display(Name = "Manage_NewEmail")]
        public string NewEmail { get; set; }
    }

    private async Task LoadAsync(HSUser user)
    {
        string email = await _userManager.GetEmailAsync(user);
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
        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        await LoadAsync(user);
        return Page();
    }

    [RequireRecentProof]
    public async Task<IActionResult> OnPostChangeEmailAsync()
    {
        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(user);
            return Page();
        }

        string email = await _userManager.GetEmailAsync(user);
        if (Input.NewEmail != email)
        {
            string userId = await _userManager.GetUserIdAsync(user);
            string code = await _userManager.GenerateChangeEmailTokenAsync(user, Input.NewEmail);
            code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
            string callbackUrl = Url.Page(
                "/Account/ConfirmEmailChange",
                pageHandler: null,
                values: new { userId = userId, email = Input.NewEmail, code = code },
                protocol: Request.Scheme);

            // The request's culture, and correct without a lookup: the signed-in user is changing
            // their own address, so the account culture provider has already resolved their
            // preference for this request.
            EmailSendResult sendResult = await _emailSender.SendEmailAsync(
                Input.NewEmail,
                _localiser["Email_ConfirmSubject"],
                _localiser["Email_ConfirmBody", HtmlEncoder.Default.Encode(callbackUrl)]);

            StatusMessage = sendResult == EmailSendResult.Failed ?
                _localiser["Manage_EmailChangeSendFailed"] :
                _localiser["Manage_EmailChangeSent"];
            return RedirectToPage();
        }

        StatusMessage = _localiser["Manage_EmailUnchanged"];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSendVerificationEmailAsync()
    {
        HSUser user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(user);
            return Page();
        }

        string userId = await _userManager.GetUserIdAsync(user);
        string email = await _userManager.GetEmailAsync(user);
        string code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
        code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
        string callbackUrl = Url.Page(
            "/Account/ConfirmEmail",
            pageHandler: null,
            values: new { userId = userId, code = code },
            protocol: Request.Scheme);
        EmailSendResult sendResult = await _emailSender.SendEmailAsync(
            email,
            _localiser["Email_ConfirmSubject"],
            _localiser["Email_ConfirmBody", HtmlEncoder.Default.Encode(callbackUrl)]);

        StatusMessage = sendResult == EmailSendResult.Failed ?
            _localiser["Manage_VerificationSendFailed"] :
            _localiser["Account_VerificationSent"];
        return RedirectToPage();
    }
}
