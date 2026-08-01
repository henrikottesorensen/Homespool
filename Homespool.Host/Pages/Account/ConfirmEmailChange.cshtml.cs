#nullable disable

using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.Services;
using Homespool.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Pages.Account;

/// <summary>
/// Applies an email change that <c>Account/Manage/Email</c> requested and emailed a link for.
/// </summary>
/// <remarks>
/// <para>
/// Identity.UI used to supply this page. Removing that package left the request half in place
/// with nothing at the other end: the link went to a route that did not exist, and no code
/// anywhere called <see cref="UserManager{TUser}.ChangeEmailAsync"/>, so an address could never
/// actually change.
/// </para>
/// <para>
/// Anonymous by design, like <see cref="ConfirmEmailModel"/>: the link is followed from a mail
/// client, which may not be the browser holding the session, and the token is what proves the
/// request is genuine.
/// </para>
/// </remarks>
[AllowAnonymous]
public class ConfirmEmailChangeModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly SignInManager<HSUser> _signInManager;
    private readonly UnitOfWork _unitOfWork;
    private readonly IOptions<Services.SmtpOptions> _smtp;

    public ConfirmEmailChangeModel(UserManager<HSUser> userManager,
                                   SignInManager<HSUser> signInManager,
                                   UnitOfWork unitOfWork,
                                   IOptions<Services.SmtpOptions> smtp)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _unitOfWork = unitOfWork;
        _smtp = smtp;
    }

    [TempData]
    public string StatusMessage { get; set; }

    /// <summary>
    /// Tells an administrator that health alerts keep going to the old address until a restart.
    /// </summary>
    /// <remarks>
    /// <see cref="Services.TelemetryAlertService"/> reads the administrator list once and caches
    /// it, deliberately, because the alert most worth sending is the one about the database being
    /// unreachable - looking recipients up at send time would fail exactly then. The cost of that
    /// choice is this staleness, and this is the one moment someone can do something about it, so
    /// it is said here rather than left to be discovered when an alert goes missing.
    /// </remarks>
    private static string AlertRecipientNotice(bool isAlertRecipient)
    {
        return isAlertRecipient
            ? " Service health alerts will continue to go to your previous address until the service is restarted."
            : string.Empty;
    }

    public async Task<IActionResult> OnGetAsync(string userId, string email, string code, CancellationToken cancellationToken)
    {
        if (userId == null || email == null || code == null)
        {
            return RedirectToPage("/Index");
        }

        HSUser user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return NotFound($"Unable to load user with ID '{userId}'.");
        }

        code = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));

        // The email and the username move together or not at all. They are two round trips through
        // UserManager, so without this the first can land and the second fail - leaving the account
        // signing in under the old address while displaying the new one, which is a split identity
        // rather than a failed change. The failure is reachable rather than theoretical: another
        // account may already hold that username, which SetUserNameAsync rejects and
        // ChangeEmailAsync never checks for.
        await using (IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            IdentityResult result = await _userManager.ChangeEmailAsync(user, email, code);

            if (!result.Succeeded)
            {
                StatusMessage = "Error changing email.";

                return Page();
            }

            // The username is set alongside the email deliberately. Accounts here are created with
            // the two identical (see Register and Setup), and sign-in is by username - so changing
            // only the email would leave the account signing in under the old address, which reads
            // as the change silently not having worked.
            IdentityResult setUserName = await _userManager.SetUserNameAsync(user, email);

            if (!setUserName.Succeeded)
            {
                // Rolled back on the way out, so this really is "nothing changed" - it used to say
                // the email had changed anyway, because it had.
                StatusMessage = "Error changing email: that address is not available.";

                return Page();
            }

            await transaction.CommitAsync(cancellationToken);
        }

        // Refreshes the cookie so the session reflects the new name rather than going stale
        // against a principal that no longer matches the user.
        await _signInManager.RefreshSignInAsync(user);

        StatusMessage = "Thank you for confirming your email change." + AlertRecipientNotice(await IsAlertRecipientAsync(user));

        return Page();
    }

    /// <summary>
    /// Whether this user receives the service's health alerts, which only administrators do, and
    /// only when there is a mail server to send them through.
    /// </summary>
    private async Task<bool> IsAlertRecipientAsync(HSUser user)
    {
        return _smtp.Value.IsConfigured
        && await _userManager.IsInRoleAsync(user, Services.AdminBootstrap.AdminRole);
    }
}
