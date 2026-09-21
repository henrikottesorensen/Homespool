#nullable disable

using System;
using System.Threading.Tasks;

using Homespool.Host.Authentication;
using Homespool.Host.Accounts;
using Homespool.Host.Localisation;
using Homespool.Host.Mail;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
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
/// <para>
/// <b>The old address is told once the change lands.</b> The address is where a forgotten password is
/// sent, so moving it is the last step of a takeover, and the confirmation link goes only to the new
/// address - which is the attacker's own if they chose it. Without a notice the owner learns of it when
/// a reset they did not ask for never arrives. Sent when the change is applied rather than when it is
/// requested, because a request that is never confirmed changes nothing, and written in the account's
/// language rather than the request's, because whoever follows the link is not necessarily its owner.
/// The notice does not name the new address: its job is to raise the alarm, and the administrator who
/// answers it sees the address on the account.
/// </para>
/// </remarks>
[AllowAnonymous]
public class ConfirmEmailChangeModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly LocalSignIn _signIn;
    private readonly IOptions<SmtpOptions> _smtp;
    private readonly IEmailSender _emailSender;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ILogger<ConfirmEmailChangeModel> _logger;

    public ConfirmEmailChangeModel(UserManager<HSUser> userManager,
                                   LocalSignIn signIn,
                                   IOptions<SmtpOptions> smtp,
                                   IEmailSender emailSender,
                                   IStringLocalizer<SharedResource> localiser,
                                   ILogger<ConfirmEmailChangeModel> logger)
    {
        _userManager = userManager;
        _signIn = signIn;
        _smtp = smtp;
        _emailSender = emailSender;
        _localiser = localiser;
        _logger = logger;
    }

    [TempData]
    public string StatusMessage { get; set; }

    /// <summary>
    /// Tells an administrator that health alerts keep going to the old address until a restart.
    /// </summary>
    /// <remarks>
    /// <see cref="Health.TelemetryAlertService"/> reads the administrator list once and caches
    /// it, deliberately, because the alert most worth sending is the one about the database being
    /// unreachable - looking recipients up at send time would fail exactly then. The cost of that
    /// choice is this staleness, and this is the one moment someone can do something about it, so
    /// it is said here rather than left to be discovered when an alert goes missing.
    /// </remarks>
    private string AlertRecipientNotice(bool isAlertRecipient)
    {
        return isAlertRecipient ? " " + _localiser["Account_AlertRecipientNotice"].Value : string.Empty;
    }

    public async Task<IActionResult> OnGetAsync(Guid? userUuid, string email, string code)
    {
        if (userUuid == null || email == null || code == null)
        {
            return RedirectToPage("/Index");
        }

        HSUser user = await _userManager.Users.SingleOrDefaultAsync(candidate => candidate.Uuid == userUuid);
        if (user == null)
        {
            return NotFound();
        }

        string token = EmailedToken.Decode(code);

        if (token is null)
        {
            // As ConfirmEmail: a code that is not a code is a failed change, not a server error.
            StatusMessage = _localiser["Account_EmailChangeError"];

            return Page();
        }

        // Read before the change, which overwrites it: the notice goes to the address being left.
        string previous = user.Email;

        // One round trip, so no transaction: SaveChangesAsync is already transactional.
        // It used to need one because the username was the email and had to
        // move with it - two UserManager calls that could half-land, leaving an account signing in
        // under the old address while displaying the new one. The username is now the person's own and
        // an address change does not touch it, so the pairing that needed the transaction is gone
        // rather than the guarantee it bought.
        IdentityResult result;

        try
        {
            result = await _userManager.ChangeEmailAsync(user, email, token);
        }
        catch (DbUpdateException)
        {
            // An address another account already holds normally comes back as a failed IdentityResult:
            // RequireUniqueEmail makes the validator refuse it before the write. This catch is for
            // losing the race to it - the other account takes the address between that check and this
            // insert, and the unique index on NormalizedEmail refuses the write instead. A change that
            // did not happen, told to the person as one, rather than a 500 on a link from their mail.
            StatusMessage = _localiser["Account_EmailChangeError"];

            return Page();
        }

        if (!result.Succeeded)
        {
            StatusMessage = _localiser["Account_EmailChangeError"];

            return Page();
        }

        // Refreshes the cookie so the session reflects the new address rather than going stale
        // against a principal that no longer matches the user.
        await _signIn.RefreshSignInAsync(HttpContext, user);

        await TellThePreviousAddressAsync(user, previous, email);

        StatusMessage = _localiser["Account_EmailChangeThanks"].Value + AlertRecipientNotice(await IsAlertRecipientAsync(user));

        return Page();
    }

    /// <summary>
    /// Mails <paramref name="previous"/> that the account's address has moved, when there was one and
    /// it is not the address just confirmed.
    /// </summary>
    /// <remarks>
    /// <b>A failed send does not undo the change</b>, and the page does not mention the notice either
    /// way: the reader here is whoever holds the new address, and the notice is not for them. A failure
    /// is logged, since it means the owner was not told.
    /// </remarks>
    private async Task TellThePreviousAddressAsync(HSUser user, string previous, string confirmed)
    {
        if (string.IsNullOrEmpty(previous) || string.Equals(previous, confirmed, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        (string subject, string body) = UserCultures.InCulture(user.Language, () => (
            _localiser["Email_AddressChangedSubject"].Value,
            _localiser["Email_AddressChangedBody"].Value));

        if (await _emailSender.SendEmailAsync(previous, subject, body) == EmailSendResult.Failed)
        {
            _logger.LogWarning("The address of user {UserId} changed, and the notice to the previous address could not be sent.", user.Id);
        }
    }

    /// <summary>
    /// Whether this user receives the service's health alerts, which only administrators do, and
    /// only when there is a mail server to send them through.
    /// </summary>
    private async Task<bool> IsAlertRecipientAsync(HSUser user)
    {
        return _smtp.Value.IsConfigured &&
               await _userManager.IsInRoleAsync(user, Accounts.AdminBootstrap.AdminRole);
    }
}
