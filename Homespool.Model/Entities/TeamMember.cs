namespace Homespool.Model.Entities;

/// <summary>
/// A user's membership in a <see cref="Team"/>, carrying their permissions on it and whether it is
/// that user's default team. Composite primary key <c>(TeamId, UserId)</c>: one row per user per
/// team.
/// </summary>
/// <remarks>
/// Permissions are three independent booleans rather than a role enum because that is exactly the
/// shape the Prusa mobile API exposes in <c>User.teams[]</c> and <c>Printer-read</c>
/// (<c>canRead</c>/<c>canUse</c>/<c>canManage</c>). A role enum would need a translation layer in
/// both directions for nothing gained.
/// </remarks>
public class TeamMember
{
    public int TeamId { get; set; }

    public virtual Team? Team { get; set; }

    /// <summary>User id of the member. A plain id, not a foreign key — see <see cref="Team.CreatedBy"/>.</summary>
    public long UserId { get; set; }

    public bool CanRead { get; set; }

    public bool CanUse { get; set; }

    public bool CanManage { get; set; }

    /// <summary>
    /// Whether this team is the user's default — where their printers land, and the fallback when a
    /// claim omits a team id.
    /// </summary>
    /// <remarks>
    /// Exactly one membership per user has this set, enforced by a filtered unique index on
    /// <c>(UserId) WHERE IsDefault</c> (see <c>HomespoolDbContext</c>). The flag lives here rather than as
    /// <c>HSUser.DefaultTeamId</c> to avoid a circular foreign key
    /// (User → Team → TeamMember → User) and the two-step insert it would force at user creation.
    /// </remarks>
    public bool IsDefault { get; set; }
}
