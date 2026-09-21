using System;

using Homespool.Host.Accounts;

namespace Homespool.Host.DTO;

/// <summary>Somebody named in a response: their handle and their name.</summary>
/// <remarks>
/// An object rather than a bare name, because a name is display text and the handle is what a client
/// can compare - with <c>GET /api/v1/user</c>'s own <c>uuid</c>, for one.
/// </remarks>
public class UserReferenceReadDTO
{
    public required Guid Uuid { get; set; }

    public required string UserName { get; set; }

    public static UserReferenceReadDTO FromReference(UserReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        return new()
        {
            Uuid = reference.Uuid,
            UserName = reference.UserName,
        };
    }
}
