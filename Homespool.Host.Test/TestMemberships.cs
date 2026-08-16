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
    /// The capability set equivalent to the old read/use/manage triple, for the fixtures that were
    /// written against it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Additive, not graded</b>, so the odd combinations survive translation. Several tests turn
    /// <i>read</i> off while leaving the others on - which the three booleans allowed and no preset
    /// does - and those tests are the ones checking that a printer stays invisible without it. A
    /// grade-shaped translation quietly grants the missing capability back and the test passes for
    /// the wrong reason.
    /// </para>
    /// <para>
    /// <b>Written literally rather than through <see cref="CapabilitySet.Format"/></b>, because those
    /// same combinations are no longer <i>writable</i>: an act implies the base view, so formatting
    /// <c>Print</c> without <c>ViewPrinter</c> puts <c>ViewPrinter</c> back. A literal row is what a
    /// column written before the closure rule looks like - so the two tests go on proving what they
    /// were written to prove, and now also prove the closure is applied on the way <i>in</i> rather
    /// than on the way out.
    /// </para>
    /// </remarks>
    public static string Graded(bool canRead, bool canUse, bool canManage)
    {
        System.Collections.Generic.List<Capability> capabilities = [];

        if (canRead)
        {
            capabilities.AddRange(CapabilityPresets.Viewer);
        }

        if (canUse)
        {
            capabilities.Add(Capability.Print);
            capabilities.Add(Capability.ControlPrinter);
        }

        if (canManage)
        {
            capabilities.Add(Capability.ManagePrinter);
            capabilities.Add(Capability.ManageCamera);
        }

        // Deliberately not CapabilitySet.Format - see the remarks above.
        return string.Join(' ', capabilities.Distinct().OrderBy(capability => capability));
    }

    public static TeamMember With(int teamId, long userId, params Capability[] capabilities)
    {
        return With(teamId, userId, (System.Collections.Generic.IReadOnlyList<Capability>)capabilities);
    }

    private static TeamMember With(int teamId,
                                   long userId,
                                   System.Collections.Generic.IReadOnlyList<Capability> capabilities)
    {
        return new TeamMember
        {
            TeamId = teamId,
            UserId = userId,
            Capabilities = CapabilitySet.Format(capabilities),
        };
    }
}
