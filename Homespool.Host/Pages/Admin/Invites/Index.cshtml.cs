using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

using Homespool.Host.Localisation;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Admin.Invites;

/// <summary>
/// Admin-only list of invitations, with a revoke action. Revoke is a soft-expire (see
/// <see cref="InvitationService.RevokeAsync"/>), so a revoked invite shows here as "Expired".
/// </summary>
[Authorize(Roles = AdminBootstrap.AdminRole)]
public class IndexModel : PageModel
{
    private readonly InvitationService _invitationService;
    private readonly TeamService _teamService;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public IndexModel(InvitationService invitationService, TeamService teamService,
                      IStringLocalizer<SharedResource> localiser)
    {
        _invitationService = invitationService;
        _teamService = teamService;
        _localiser = localiser;
    }

    public IReadOnlyList<Invitation> Invitations { get; private set; } = [];

    private IReadOnlyDictionary<int, string> _teamNames = new Dictionary<int, string>();

    [TempData]
    public string? StatusMessage { get; set; }

    /// <summary>Outstanding / Used / Expired, derived from the invite's timestamps.</summary>
    public static string StatusOf(Invitation invitation)
    {
        if (invitation.UsedAt is not null)
        {
            return "Used";
        }

        return invitation.ExpiresAt <= DateTimeOffset.UtcNow ? "Expired" : "Outstanding";
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Invitations = await _invitationService.ListAsync(cancellationToken);

        Dictionary<int, string> names = new();
        foreach (Team team in await _teamService.GetAllTeamsAsync(cancellationToken))
        {
            names[team.Id] = team.Name ?? _localiser["Common_TeamNumbered", team.Id].Value;
        }

        _teamNames = names;
    }

    public async Task<IActionResult> OnPostRevokeAsync(int id, CancellationToken cancellationToken)
    {
        await _invitationService.RevokeAsync(id, cancellationToken);
        StatusMessage = _localiser["Invites_Revoked"];

        return RedirectToPage();
    }

    /// <summary>Human label for the invite's target: an existing team, or a brand-new account.</summary>
    public string TargetOf(Invitation invitation)
    {
        if (invitation.TeamId is not int teamId)
        {
            return _localiser["Invites_NewAccount"];
        }

        return _teamNames.TryGetValue(teamId, out string? name) ?
            name :
            _localiser["Common_TeamNumbered", teamId].Value;
    }
}
