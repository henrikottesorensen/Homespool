using System.Collections.Generic;
using System.Linq;

using PrinterService.Model.Entities;

namespace PrinterService.Host.PrusaConnect.DTO.App;

/// <summary>
/// The app-facing user read shape (Connect's <c>User</c>). <see cref="Name"/> is the account's
/// <c>UserName</c> - which is always the email address, since every account is created that way
/// (invite acceptance and <c>/setup</c> both call <c>SetUserNameAsync(user, email, ...)</c>) -
/// there being no separate display-name concept on <see cref="PSUser"/> yet.
/// </summary>
public class UserReadDTO
{
    public required long Id { get; set; }

    public required string Name { get; set; }

    public string? Email { get; set; }

    public required IReadOnlyList<TeamMembershipDTO> Teams { get; set; }

    public static UserReadDTO FromEntity(PSUser user, IReadOnlyList<TeamMember> memberships) => new()
    {
        Id = user.Id,
        Name = user.UserName ?? user.Email ?? string.Empty,
        Email = user.Email,
        Teams = memberships.Select(TeamMembershipDTO.FromEntity).ToList(),
    };
}
