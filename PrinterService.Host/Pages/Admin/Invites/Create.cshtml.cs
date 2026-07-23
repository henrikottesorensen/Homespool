using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

using PrinterService.Host.Services;
using PrinterService.Model.Entities;

namespace PrinterService.Host.Pages.Admin.Invites;

/// <summary>
/// Admin-only page that issues an invitation: mints a token, stores its hash, mails the accept link,
/// and shows the link back so the admin can copy it (needed when SMTP is not configured). The invite
/// either mints a new account with its own default team (no team selected) or adds the invitee to an
/// existing team (AGENT-NOTES phase-1.5 §15 step 6).
/// </summary>
[Authorize(Roles = AdminBootstrap.AdminRole)]
public class CreateModel : PageModel
{
    private readonly InvitationService _invitationService;
    private readonly TeamService _teamService;
    private readonly UserManager<PSUser> _userManager;
    private readonly IEmailSender _emailSender;
    private readonly ILogger<CreateModel> _logger;

    public CreateModel(
        InvitationService invitationService,
        TeamService teamService,
        UserManager<PSUser> userManager,
        IEmailSender emailSender,
        ILogger<CreateModel> logger)
    {
        _invitationService = invitationService;
        _teamService = teamService;
        _userManager = userManager;
        _emailSender = emailSender;
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
        [Display(Name = "Email")]
        public string Email { get; set; } = string.Empty;

        [Display(Name = "Team")]
        public int? TeamId { get; set; }

        [Range(1, 8760, ErrorMessage = "Expiry must be between 1 and 8760 hours.")]
        [Display(Name = "Expires in (hours, optional)")]
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

        PSUser? admin = await _userManager.GetUserAsync(User);
        if (admin is null)
        {
            // [Authorize] should make this unreachable; fail closed rather than invent an inviter id.
            return Forbid();
        }

        DateTimeOffset? expiresAt = Input.ExpiresInHours is int hours
            ? DateTimeOffset.UtcNow + TimeSpan.FromHours(hours)
            : null;

        (Invitation invitation, string plaintextToken) = await _invitationService.CreateAsync(
            Input.Email, Input.TeamId, admin.Id, expiresAt, cancellationToken);

        string code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(plaintextToken));
        AcceptLink = Url.Page(
            "/Account/Register",
            pageHandler: null,
            values: new { inviteId = invitation.Id, code },
            protocol: Request.Scheme);

        EmailSendResult sendResult = await _emailSender.SendEmailAsync(
            Input.Email,
            "You're invited to PrinterService",
            $"You have been invited. Accept by <a href='{HtmlEncoder.Default.Encode(AcceptLink!)}'>clicking here</a>. " +
            $"This invitation expires {invitation.ExpiresAt.ToLocalTime():g}.");

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
            new SelectListItem("New account (its own team)", string.Empty),
        ];

        foreach (Team team in await _teamService.GetAllTeamsAsync(cancellationToken))
        {
            options.Add(new SelectListItem(team.Name ?? $"Team #{team.Id}", team.Id.ToString()));
        }

        TeamOptions = options;
    }
}
