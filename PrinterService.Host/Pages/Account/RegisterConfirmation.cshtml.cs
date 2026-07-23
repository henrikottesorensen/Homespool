// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
#nullable disable

using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

using PrinterService.Host.Services;
using PrinterService.Model.Entities;

namespace PrinterService.Host.Pages.Account
{
    [AllowAnonymous]
    public class RegisterConfirmationModel : PageModel
    {
        private readonly UserManager<PSUser> _userManager;
        private readonly SmtpOptions _smtpOptions;

        public RegisterConfirmationModel(UserManager<PSUser> userManager, IOptions<SmtpOptions> smtpOptions)
        {
            _userManager = userManager;
            _smtpOptions = smtpOptions.Value;
        }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public string Email { get; set; }

        /// <summary>
        /// Whether outgoing mail is configured. When it is not, the account was created already confirmed and
        /// there is nothing for the user to wait for.
        /// </summary>
        public bool SmtpConfigured { get; set; }

        /// <summary>
        /// The confirmation mail could not be sent, so the account cannot be confirmed without operator help.
        /// </summary>
        public bool EmailFailed { get; set; }

        public async Task<IActionResult> OnGetAsync(string email, string returnUrl = null, bool emailFailed = false)
        {
            if (email == null)
            {
                return RedirectToPage("/Index");
            }
            returnUrl = returnUrl ?? Url.Content("~/");

            PSUser user = await _userManager.FindByEmailAsync(email);
            if (user == null)
            {
                return NotFound($"Unable to load user with email '{email}'.");
            }

            Email = email;
            EmailFailed = emailFailed;
            SmtpConfigured = _smtpOptions.IsConfigured;

            // The stock scaffold rendered a working confirmation link here whenever no real email sender was
            // registered. That is removed deliberately, and there is no mode in which it should come back:
            // with SMTP configured it would let anyone confirm an address they do not control, bypassing
            // confirmation entirely; without SMTP the account is already created confirmed, so the link
            // confirms something that needs no confirming.

            return Page();
        }
    }
}
