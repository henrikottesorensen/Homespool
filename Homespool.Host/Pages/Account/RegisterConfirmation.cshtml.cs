// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using Homespool.Host.Mail;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Pages.Account;

[AllowAnonymous]
public class RegisterConfirmationModel : PageModel
{
    private readonly SmtpOptions _smtpOptions;

    public RegisterConfirmationModel(IOptions<SmtpOptions> smtpOptions)
    {
        _smtpOptions = smtpOptions.Value;
    }

    /// <summary>
    /// Whether outgoing mail is configured. When it is not, the account was created already confirmed and
    /// there is nothing for the user to wait for.
    /// </summary>
    public bool SmtpConfigured { get; set; }

    /// <summary>
    /// The confirmation mail could not be sent, so the account cannot be confirmed without operator help.
    /// </summary>
    public bool EmailFailed { get; set; }

    public IActionResult OnGet(string email, bool emailFailed = false)
    {
        if (email == null)
        {
            return RedirectToPage("/Index");
        }

        // The address is deliberately never looked up. This page is anonymous, and answering an unknown
        // address differently from a registered one would be an account-existence oracle - the leak the
        // login and forgot-password flows go out of their way to avoid. Nothing rendered here needs the
        // account: the page only relays how registration left things, via its query parameters.
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
