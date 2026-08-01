using Homespool.Model.Entities;

namespace Homespool.Host.PrusaConnect.DTO.App;

/// <summary>
/// One entry of <c>User.teams[]</c> from Connect's mobile API - a team the user belongs to, with
/// their permissions on it.
/// </summary>
public class TeamMembershipDTO
{
    public required int Id { get; set; }

    public string? Name { get; set; }

    public required bool CanRead { get; set; }

    public required bool CanUse { get; set; }

    public required bool CanManage { get; set; }

    public static TeamMembershipDTO FromEntity(TeamMember member)
    {
        return new()
        {
            Id = member.TeamId,
            Name = member.Team?.Name,
            CanRead = member.CanRead,
            CanUse = member.CanUse,
            CanManage = member.CanManage,
        };
    }
}
