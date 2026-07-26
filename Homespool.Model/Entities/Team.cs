using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Homespool.Model.Entities;

/// <summary>
/// A group that owns printers. Every user gets one at creation (their <em>default</em> team) and
/// can belong to several; printers belong to a team rather than to a user.
/// </summary>
/// <remarks>
/// There is no separate class of "personal" team — a user's first team starts with one member but
/// is an ordinary team that others can be invited into (AGENT-NOTES phase-1.5 §15). The
/// "this is my default" flag therefore lives on <see cref="TeamMember"/>, not here: it describes a
/// user's relationship to a team, and the same team can be default for its creator while just
/// another membership for someone invited in later.
/// </remarks>
public class Team
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// User-chosen display name. Null means the user has not named it — resolve for display as
    /// <c>Name ?? "&lt;CreatedBy display name&gt;'s team"</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately not defaulted to a generated string at creation, on the same reasoning as
    /// <see cref="Printer.Name"/> (§13): storing "Henrik's team" makes "the user chose this"
    /// indistinguishable from "we generated it", so the generated form could never be safely
    /// refreshed once the creator renamed themselves. That is what <see cref="CreatedBy"/> is for.
    /// </remarks>
    public string? Name { get; set; }

    /// <summary>
    /// User id of whoever created the team. Audit, and the source of the generated display name
    /// when <see cref="Name"/> is null.
    /// </summary>
    /// <remarks>
    /// A plain user id, not a foreign key — mirroring the original <c>Printer.Owner</c>. This is a
    /// self-hosted, single-tenant service (§1); referential integrity to the Identity user table
    /// buys little here and would entangle team lifetime with user deletion, which has no flow yet.
    /// </remarks>
    public long CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The team's memberships. A team always has at least its creator.</summary>
    public virtual ICollection<TeamMember> Members { get; set; } = [];
}
