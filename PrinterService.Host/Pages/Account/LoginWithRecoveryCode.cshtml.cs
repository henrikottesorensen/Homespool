// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
#nullable disable

using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;

using PrinterService.Model.Entities;

namespace PrinterService.Host.Pages.Account
{
    [AllowAnonymous]
    public class LoginWithRecoveryCodeModel : PageModel
    {
        private readonly SignInManager<PSUser> _signInManager;
        private readonly UserManager<PSUser> _userManager;
        private readonly ILogger<LoginWithRecoveryCodeModel> _logger;

        public LoginWithRecoveryCodeModel(SignInManager<PSUser> signInManager,
                                          UserManager<PSUser> userManager,
                                          ILogger<LoginWithRecoveryCodeModel> logger)
        {
            _signInManager = signInManager;
            _userManager = userManager;
            _logger = logger;
        }

        [BindProperty]
        public InputModel Input { get; set; }

        public string ReturnUrl { get; set; }

        public class InputModel
        {
            [BindProperty]
            [Required]
            [DataType(DataType.Text)]
            [Display(Name = "Recovery Code")]
            public string RecoveryCode { get; set; }
        }

        public async Task<IActionResult> OnGetAsync(string returnUrl = null)
        {
            // Ensure the user has gone through the username & password screen first.
            PSUser user = await _signInManager.GetTwoFactorAuthenticationUserAsync();
            if (user is null)
            {
                throw new InvalidOperationException("Unable to load two-factor authentication user.");
            }

            ReturnUrl = returnUrl;

            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string returnUrl = null)
        {
            if (!ModelState.IsValid)
            {
                return Page();
            }

            PSUser user = await _signInManager.GetTwoFactorAuthenticationUserAsync();
            if (user is null)
            {
                throw new InvalidOperationException("Unable to load two-factor authentication user.");
            }

            string recoveryCode = Input.RecoveryCode.Replace(" ", string.Empty, StringComparison.Ordinal);

            Microsoft.AspNetCore.Identity.SignInResult result =
                await _signInManager.TwoFactorRecoveryCodeSignInAsync(recoveryCode);

            string userId = await _userManager.GetUserIdAsync(user);

            if (result.Succeeded)
            {
                _logger.LogInformation("User with ID {UserId} logged in with a recovery code.", userId);

                return LocalRedirect(returnUrl ?? Url.Content("~/"));
            }

            if (result.IsLockedOut)
            {
                _logger.LogWarning("User with ID {UserId} account locked out.", userId);

                return RedirectToPage("./Lockout");
            }

            _logger.LogWarning("Invalid recovery code entered for user with ID {UserId}", userId);
            ModelState.AddModelError(string.Empty, "Invalid recovery code entered.");

            return Page();
        }
    }
}
