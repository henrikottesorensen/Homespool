using System;
using System.Collections.Generic;
using System.Linq;

using Homespool.Model;
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
    public required Guid Uuid { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// The account's email address, or null when the credential's scope does not include
    /// <see cref="Capability.ViewAccountDetails"/>.
    /// </summary>
    public string? Email { get; set; }

    public required IReadOnlyList<TeamMembershipDTO> Teams { get; set; }

    /// <summary>
    /// The printer this person has chosen as their default, or null when they have chosen none - or
    /// chose one they can no longer see.
    /// </summary>
    /// <remarks>
    /// <b>Read-only, and a preference rather than a destination.</b> The pages use it to pre-select a
    /// printer; nothing in this API sends work to it, and every call that acts on a printer still names
    /// one.
    /// </remarks>
    public Guid? DefaultPrinterUuid { get; set; }

    /// <summary>Maps an account for <paramref name="caller"/>, who is that account.</summary>
    /// <remarks>
    /// <b>The address is withheld from the name's fallback as well</b>, or a caller without
    /// <see cref="Capability.ViewAccountDetails"/> would be handed it in the other field.
    /// </remarks>
    public static UserReadDTO FromEntity(HSUser user,
                                         IReadOnlyList<TeamMember> memberships,
                                         Guid? defaultPrinterUuid,
                                         Caller caller)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(caller);

        string? email = caller.Allows(Capability.ViewAccountDetails) ? user.Email : null;

        return new()
        {
            Uuid = user.Uuid,
            Name = user.UserName ?? email ?? string.Empty,
            Email = email,
            Teams = memberships.Select(member => TeamMembershipDTO.FromEntity(member, caller)).ToList(),
            DefaultPrinterUuid = defaultPrinterUuid,
        };
    }
}
