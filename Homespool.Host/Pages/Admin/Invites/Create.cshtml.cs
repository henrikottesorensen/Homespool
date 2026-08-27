using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

using Homespool.Host.Localisation;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Admin.Invites;

/// <summary>
/// Admin-only page that issues an invitation: mints a token, stores its hash, mails the accept link,
/// and shows the link back so the admin can copy it (needed when SMTP is not configured). The invite
/// either mints a new account with its own default team (no team selected) or adds the invitee to an
/// existing team.
/// </summary>
[Authorize(Roles = AdminBootstrap.AdminRole)]
public class CreateModel : PageModel
{
    private readonly InvitationService _invitationService;
    private readonly TeamService _teamService;
    private readonly UserManager<HSUser> _userManager;
    private readonly IEmailSender _emailSender;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ILogger<CreateModel> _logger;

    public CreateModel(InvitationService invitationService,
                       TeamService teamService,
                       UserManager<HSUser> userManager,
                       IEmailSender emailSender,
                       IStringLocalizer<SharedResource> localiser,
                       ILogger<CreateModel> logger)
    {
        _invitationService = invitationService;
        _teamService = teamService;
        _userManager = userManager;
        _emailSender = emailSender;
        _localiser = localiser;
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<SelectListItem> TeamOptions { get; private set; } = [];

    /// <summary>Set after a successful create, so the view can show (and the admin can copy) the link.</summary>
    public string? AcceptLink { get; private set; }

    /// <summary>Whether the invite email was actually sent (false when SMTP is off or the send failed).</summary>
    public bool EmailSent { get; private set; }

    public class InputModel
    {
        [Required]
        [EmailAddress]
        [Display(Name = "Account_Email")]
        public string Email { get; set; } = string.Empty;

        [Display(Name = "Common_Team")]
        public int? TeamId { get; set; }

        [Range(1, 8760, ErrorMessage = "Validation_ExpiryRange")]
        [Display(Name = "Invites_ExpiresIn")]
        public int? ExpiresInHours { get; set; }
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadTeamOptionsAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await LoadTeamOptionsAsync(cancellationToken);

        if (!ModelState.IsValid)
        {
            return Page();
        }

        HSUser? admin = await _userManager.GetUserAsync(User);
        if (admin is null)
        {
            // [Authorize] should make this unreachable; fail closed rather than invent an inviter id.
            return Forbid();
        }

        DateTimeOffset? expiresAt = Input.ExpiresInHours is int hours ? DateTimeOffset.UtcNow + TimeSpan.FromHours(hours) : null;

        (Invitation invitation, string plaintextToken) = await _invitationService.CreateAsync(
            Input.Email, Input.TeamId, admin.Id, expiresAt, cancellationToken);

        string code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(plaintextToken));
        AcceptLink = Url.Page(
            "/Account/Register",
            pageHandler: null,
            values: new { inviteId = invitation.Id, code },
            protocol: Request.Scheme);

        // Composed in the administrator's language, which is the request's, because an invitee has no
        // account and therefore nothing stored to read. That is a limit of the invitation rather than
        // an oversight: the only way to do better is to let the inviter say which language to write in.
        //
        // The expiry is formatted in that same culture rather than InvariantCulture as before. It is
        // read by a person, not parsed by anything, so a Dane should see 09-03-2026 14:30.
        EmailSendResult sendResult = await _emailSender.SendEmailAsync(
            Input.Email,
            _localiser["Email_InviteSubject"],
            _localiser[
                "Email_InviteBody",
                HtmlEncoder.Default.Encode(AcceptLink!),
                invitation.ExpiresAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)]);

        EmailSent = sendResult == EmailSendResult.Sent;

        _logger.LogInformation(
            "Invitation {InviteId} created for {Email} by admin {AdminId}; email sent: {EmailSent}.",
            invitation.Id, Input.Email, admin.Id, EmailSent);

        // Reset the form for the next invite, but keep AcceptLink shown.
        ModelState.Clear();
        Input = new InputModel();

        return Page();
    }

    private async Task LoadTeamOptionsAsync(CancellationToken cancellationToken)
    {
        List<SelectListItem> options =
        [
            new SelectListItem(_localiser["Invites_NewAccountOwnTeam"], string.Empty),
        ];

        foreach (Team team in await _teamService.GetAllTeamsAsync(cancellationToken))
        {
            options.Add(new SelectListItem(
                            team.Name ?? _localiser["Common_TeamNumbered", team.Id].Value, team.Id.ToString()));
        }

        TeamOptions = options;
    }
}
