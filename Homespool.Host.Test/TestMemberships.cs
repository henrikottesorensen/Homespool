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
    /// <b>Additive, not graded</b>, so the odd combinations survive translation. Several tests turn
    /// <i>read</i> off while leaving the others on - which the three booleans allowed and no preset
    /// does - and those tests are the ones checking that a printer stays invisible without it. A
    /// grade-shaped translation quietly grants the missing capability back and the test passes for
    /// the wrong reason.
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

        return CapabilitySet.Format(capabilities);
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
