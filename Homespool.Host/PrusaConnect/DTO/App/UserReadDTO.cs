using System.Collections.Generic;
using System.Linq;

using Homespool.Model.Entities;

namespace Homespool.Host.PrusaConnect.DTO.App;

/// <summary>
/// The app-facing user read shape (Connect's <c>User</c>). <see cref="Name"/> is the account's
/// username, which is a name the person chose rather than their address - the email is its own field
/// here, and a client that wants one asks for it.
/// </summary>
/// <remarks>
/// The fallback to the email exists only because <c>UserName</c> is nullable on
/// <c>IdentityUser{TKey}</c>. Nothing this application creates leaves it unset: every account gets a
/// username at creation, and Identity's own validator refuses an empty one.
/// </remarks>
public class UserReadDTO
{
    public required long Id { get; set; }

    public required string Name { get; set; }

    public string? Email { get; set; }

    public required IReadOnlyList<TeamMembershipDTO> Teams { get; set; }

    public static UserReadDTO FromEntity(HSUser user, IReadOnlyList<TeamMember> memberships)
    {
        return new()
        {
            Id = user.Id,
            Name = user.UserName ?? user.Email ?? string.Empty,
            Email = user.Email,
            Teams = memberships.Select(TeamMembershipDTO.FromEntity).ToList(),
        };
    }
}
