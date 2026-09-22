using System;
using System.Collections.Generic;
using System.Linq;

using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.PrusaConnect.DTO.App;

/// <summary>
/// A team the user belongs to, with what their membership permits.
/// </summary>
/// <remarks>
/// Was one entry of <c>User.teams[]</c> from Connect's mobile API, carrying that shape's three
/// booleans. The compatibility goal is gone, and the permissions are a capability list now.
/// </remarks>
public class TeamMembershipDTO
{
    public required Guid Uuid { get; set; }

    public string? Name { get; set; }

    /// <summary>
    /// What the caller may do on this team - <c>Capability</c> names, as the membership grants them
    /// and the credential's scope leaves them.
    /// </summary>
    public required IReadOnlyList<string> Capabilities { get; set; }

    /// <summary>Maps <paramref name="member"/>, narrowed to what <paramref name="caller"/> may use.</summary>
    /// <remarks>
    /// The same intersection <c>PrinterQueryService</c> applies to a printer's capabilities, so the
    /// team and the printers on it report the same rights to the same token. A team left with nothing
    /// is still listed: the token belongs to a member, and its list being empty is the true answer.
    /// </remarks>
    public static TeamMembershipDTO FromEntity(TeamMember member, Caller caller)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(caller);

        return new()
        {
            Uuid = member.Team!.Uuid,
            Name = member.Team?.Name,
            Capabilities = CapabilitySet.Parse(member.Capabilities)
                                        .Intersect(caller.Scope)
                                        .Granted
                                        .OrderBy(capability => capability)
                                        .Select(capability => capability.ToString())
                                        .ToList(),
        };
    }
}
