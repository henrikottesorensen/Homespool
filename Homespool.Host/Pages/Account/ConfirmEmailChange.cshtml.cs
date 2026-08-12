#nullable disable

using System.Text;
using System.Threading.Tasks;

using Homespool.Host.Localisation;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;
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
    private readonly IOptions<Services.SmtpOptions> _smtp;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public ConfirmEmailChangeModel(UserManager<HSUser> userManager,
                                   SignInManager<HSUser> signInManager,
                                   IOptions<Services.SmtpOptions> smtp,
                                   IStringLocalizer<SharedResource> localiser)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _smtp = smtp;
        _localiser = localiser;
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
        return isAlertRecipient ?
            " Service health alerts will continue to go to your previous address until the service is restarted." :
            string.Empty;
    }

    public async Task<IActionResult> OnGetAsync(string userId, string email, string code)
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

        // One round trip, so no transaction: SaveChangesAsync is already transactional
        // (notes/transactions.md). It used to need one because the username was the email and had to
        // move with it - two UserManager calls that could half-land, leaving an account signing in
        // under the old address while displaying the new one. The username is now the person's own and
        // an address change does not touch it, so the pairing that needed the transaction is gone
        // rather than the guarantee it bought.
        IdentityResult result = await _userManager.ChangeEmailAsync(user, email, code);

        if (!result.Succeeded)
        {
            StatusMessage = _localiser["Account_EmailChangeError"];

            return Page();
        }

        // Refreshes the cookie so the session reflects the new address rather than going stale
        // against a principal that no longer matches the user.
        await _signInManager.RefreshSignInAsync(user);

        StatusMessage = _localiser["Account_EmailChangeThanks"].Value + AlertRecipientNotice(await IsAlertRecipientAsync(user));

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
