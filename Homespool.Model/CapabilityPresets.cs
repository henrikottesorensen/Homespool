using System.Collections.Generic;

namespace Homespool.Model;

/// <summary>
/// The named capability sets a person actually picks from. <b>Presets, not roles</b> — nothing stores
/// which one was chosen, only the capabilities it wrote.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not persisted.</b> A stored role name would be a second source of truth about what
/// a membership grants, and the two would drift the first time somebody wanted a combination the
/// presets do not name. The column is the truth; these exist so a UI does not have to offer eight
/// checkboxes, and so the two places that create memberships say what they mean.
/// </para>
/// <para>
/// <b>Order matters and is cumulative</b> — each preset contains the one before it. That is a
/// property of these particular sets rather than a rule the model enforces: a membership can hold any
/// combination at all, and nothing here prevents one.
/// </para>
/// </remarks>
public static class CapabilityPresets
{
    /// <summary>Can see everything about a printer, and change nothing.</summary>
    public static IReadOnlyList<Capability> Viewer { get; } =
    [
        Capability.ViewPrinter,
        Capability.ViewQueue,
        Capability.ViewHistory,
        Capability.ViewCamera,
    ];

    /// <summary>
    /// Can put work on a printer and withdraw their own, but not touch anyone else's print or steer
    /// the machine.
    /// </summary>
    public static IReadOnlyList<Capability> Contributor { get; } = [.. Viewer, Capability.Print];

    /// <summary>Runs the printer: anyone's print, and every command the hardware takes.</summary>
    public static IReadOnlyList<Capability> Operator { get; } = [.. Contributor, Capability.ControlPrinter];

    /// <summary>
    /// Everything, including the printer's own settings and its cameras. What the creator of a team
    /// holds on it.
    /// </summary>
    public static IReadOnlyList<Capability> Manager { get; } =
    [
        .. Operator,
        Capability.ManagePrinter,
        Capability.ManageCamera,
    ];
}
