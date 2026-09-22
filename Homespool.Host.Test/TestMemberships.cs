using System.Collections.Generic;
using System.Linq;

using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Membership rows for fixtures, named after what they can do. Keeps
/// <c>CapabilitySet.Format</c> out of every arrange block.
/// </summary>
internal static class TestMemberships
{
    public static TeamMember Viewer(int teamId, long userId)
    {
        return With(teamId, userId, CapabilityPresets.Viewer);
    }

    public static TeamMember Operator(int teamId, long userId)
    {
        return With(teamId, userId, CapabilityPresets.Operator);
    }

    public static TeamMember Manager(int teamId, long userId)
    {
        return With(teamId, userId, CapabilityPresets.Manager);
    }

    /// <summary>
    /// A capability row written exactly as given, without the implication closure
    /// <see cref="CapabilitySet.Format"/> applies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>For the rows a fixture must be able to write and the application no longer can.</b> An act
    /// implies the base view, so formatting <c>Print</c> without <c>ViewPrinter</c> puts
    /// <c>ViewPrinter</c> back - and the tests checking that a printer stays invisible without it
    /// would pass for the wrong reason. A literal row is what a column written before the closure
    /// rule looks like, so those tests also prove the closure is applied on the way <i>in</i> rather
    /// than on the way out.
    /// </para>
    /// <para>
    /// <b>For a preset the two spellings are the same row</b>, since a preset is already closed.
    /// </para>
    /// </remarks>
    public static string Literal(IEnumerable<Capability> capabilities)
    {
        // Deliberately not CapabilitySet.Format - see the remarks above.
        return string.Join(' ', capabilities.Distinct().OrderBy(capability => capability));
    }

    public static TeamMember With(int teamId, long userId, params Capability[] capabilities)
    {
        return With(teamId, userId, (IReadOnlyList<Capability>)capabilities);
    }

    private static TeamMember With(int teamId,
                                   long userId,
                                   IReadOnlyList<Capability> capabilities)
    {
        return new TeamMember
        {
            TeamId = teamId,
            UserId = userId,
            Capabilities = CapabilitySet.Format(capabilities),
        };
    }
}
