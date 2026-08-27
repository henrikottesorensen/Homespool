using System.Collections.Generic;
using System.Linq;

using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;

using Homespool.Host.Localisation;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Accounts;

/// <summary>
/// Builds the team picker options shared by every page that lets a user add a printer to a team
/// they manage - <c>Pages/Printers/Add</c> and <c>Pages/Printers/Claim</c>. Extracted at the second
/// caller rather than the third, matching this codebase's own precedent
/// (<see cref="Homespool.Data.TeamProvisioning.AddDefaultTeam"/>'s remarks): a helper exists so
/// the second caller cannot drift from the first, not to wait until copy-paste becomes obviously bad.
/// </summary>
public static class TeamOptionSelectListBuilder
{
    /// <summary>
    /// Only <c>CanManage</c> memberships - adding a printer to a team is a structural change, the
    /// same permission bar <see cref="PrusaConnect.PrusaConnectService"/> enforces server-side for
    /// both provisioning and claiming. The default team (<see cref="TeamMember.IsDefault"/>) is
    /// pre-selected.
    /// </summary>
    public static IReadOnlyList<SelectListItem> BuildManageableOptions(
        IReadOnlyList<TeamMember> memberships,
        IStringLocalizer<SharedResource> localiser)
    {
        return memberships
               .AsEnumerable()
               .Where(m => CapabilitySet.Parse(m.Capabilities).Allows(Capability.ManagePrinter))
               .Select(m => new SelectListItem(
                            m.Team?.Name ?? localiser["Common_TeamNumbered", m.TeamId].Value,
                            m.TeamId.ToString(),
                            m.IsDefault))
               .ToList();
    }
}
