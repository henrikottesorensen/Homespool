using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

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

    public IndexModel(InvitationService invitationService, TeamService teamService)
    {
        _invitationService = invitationService;
        _teamService = teamService;
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
            names[team.Id] = team.Name ?? $"Team #{team.Id}";
        }

        _teamNames = names;
    }

    public async Task<IActionResult> OnPostRevokeAsync(int id, CancellationToken cancellationToken)
    {
        await _invitationService.RevokeAsync(id, cancellationToken);
        StatusMessage = "Invitation revoked.";

        return RedirectToPage();
    }

    /// <summary>Human label for the invite's target: an existing team, or a brand-new account.</summary>
    public string TargetOf(Invitation invitation)
    {
        if (invitation.TeamId is not int teamId)
        {
            return "New account";
        }

        return _teamNames.TryGetValue(teamId, out string? name) ? name : $"Team #{teamId}";
    }
}
