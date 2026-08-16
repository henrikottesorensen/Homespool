namespace Homespool.Model.Entities;

/// <summary>
/// A user's membership in a <see cref="Team"/>, carrying their permissions on it and whether it is
/// that user's default team. Composite primary key <c>(TeamId, UserId)</c>: one row per user per
/// team.
/// </summary>
public class TeamMember
{
    /// <summary>The longest <see cref="Capabilities"/> string the column has to hold.</summary>
    /// <remarks>
    /// Every capability name, single-spaced, with room for the vocabulary to roughly double. A cap at
    /// all is what stops the column being a place to put arbitrary text.
    /// </remarks>
    public const int CapabilitiesMaxLength = 512;

    public int TeamId { get; set; }

    public virtual Team? Team { get; set; }

    /// <summary>User id of the member. A plain id, not a foreign key — see <see cref="Team.CreatedBy"/>.</summary>
    public long UserId { get; set; }

    /// <summary>
    /// What this membership permits: <see cref="Capability"/> names separated by spaces, as
    /// <c>CapabilitySet</c> writes them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read it with <c>CapabilitySet.Parse</c> and write it with <c>CapabilitySet.Format</c>;
    /// never compose or match this string by hand.</b> A substring test is the specific trap — it
    /// would let one capability's name match inside another's — and hand-composition is how an
    /// unrecognised name gets in.
    /// </para>
    /// <para>
    /// <b>Empty grants nothing</b>, so an uninitialised membership refuses rather than permits.
    /// </para>
    /// <para>
    /// <b>Text rather than a bitmask</b>, so a row says what it means when somebody is reading query
    /// output trying to work out what went wrong — and so a new capability costs no migration. The
    /// price is that renaming one does.
    /// </para>
    /// </remarks>
    public string Capabilities { get; set; } = string.Empty;

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
