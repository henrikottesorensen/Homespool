using System.Collections.Generic;
using System.Linq;

using Homespool.Model.Entities;

namespace Homespool.Host.PrusaConnect.DTO.App;

/// <summary>
/// The app-facing user read shape (Connect's <c>User</c>). <see cref="Name"/> is
/// <see cref="HSUser.DisplayName"/>, falling back to <c>UserName</c> and then the email.
/// <c>UserName</c> is still always the email address - it remains the sign-in identifier - which is
/// exactly why this prefers the display name: an API called <c>Name</c> should not hand out an
/// address just because that is what people sign in with.
/// </summary>
public class UserReadDTO
{
    public required long Id { get; set; }

    public required string Name { get; set; }

    public string? Email { get; set; }

    public required IReadOnlyList<TeamMembershipDTO> Teams { get; set; }

    public static UserReadDTO FromEntity(HSUser user, IReadOnlyList<TeamMember> memberships) => new()
    {
        Id = user.Id,
        Name = user.DisplayName ?? user.UserName ?? user.Email ?? string.Empty,
        Email = user.Email,
        Teams = memberships.Select(TeamMembershipDTO.FromEntity).ToList(),
    };
}
