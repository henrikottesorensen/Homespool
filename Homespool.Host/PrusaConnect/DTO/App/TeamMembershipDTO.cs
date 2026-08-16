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
    public required int Id { get; set; }

    public string? Name { get; set; }

    /// <summary>What this membership permits - <c>Capability</c> names.</summary>
    public required IReadOnlyList<string> Capabilities { get; set; }

    public static TeamMembershipDTO FromEntity(TeamMember member)
    {
        return new()
        {
            Id = member.TeamId,
            Name = member.Team?.Name,
            Capabilities = CapabilitySet.Parse(member.Capabilities)
                                        .Granted
                                        .OrderBy(capability => capability)
                                        .Select(capability => capability.ToString())
                                        .ToList(),
        };
    }
}
