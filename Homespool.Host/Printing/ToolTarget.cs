namespace Homespool.Host.Printing;

/// <summary>
/// Which tool a gcode command carrying no <c>T</c> argument will act on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every gcode this application sends is toolless</b> - <c>M104</c>, <c>M140</c>, <c>M702</c> -
/// so on a multi-tool printer the target is whatever is currently picked, decided by firmware and
/// not by us. That is fine when something is picked and quietly wrong when nothing is:
/// <c>M104</c> and <c>M702</c> resolve from <c>PhysicalToolIndex::currently_selected()</c>, report
/// <c>MSG_INVALID_EXTRUDER</c> <b>to the serial console</b>, and return having done nothing.
/// </para>
/// <para>
/// <b>The failure is worse for the heaters than for unloading, because it is partial.</b>
/// <c>M140</c> is the bed and a bed has no tool, so it always applies. Preheat therefore heats the
/// bed and not the nozzle, and cooling leaves the nozzle hot while reporting both heaters off -
/// with the printer answering the frame <c>Accepted</c> either way. See
/// <c>notes/toolchangers.md</c> §3d.
/// </para>
/// <para>
/// <b>The wire says which case a printer is in, and it needs no new plumbing.</b> Firmware packs the
/// answer into the slot block's <c>active</c> field - <c>vt.to_raw() + 1</c> for a picked tool,
/// <b><c>0</c> for none</b> - and that reaches
/// <see cref="Homespool.Model.Entities.PrinterLiveState.ActiveSlot"/> already.
/// </para>
/// <para>
/// <b><c>0</c> is a sentinel packed into a number</b>, the same species as firmware's <c>"---"</c>
/// for no filament, and it fails the same way: a null check separates <em>single-tool</em> from
/// <em>multi-tool</em> and says nothing at all about <em>picked</em> versus <em>unpicked</em>. This
/// type exists so that distinction is made once, by something named after it, rather than at each
/// call site by whoever remembers.
/// </para>
/// </remarks>
public sealed record ToolTarget
{
    private ToolTarget(bool isMultiTool, int? pickedTool)
    {
        IsMultiTool = isMultiTool;
        PickedTool = pickedTool;
    }

    /// <summary>Whether the printer reports more than one tool.</summary>
    public bool IsMultiTool { get; }

    /// <summary>
    /// The tool a toolless command will act on, <b>1-based as the wire numbers it</b>, or null when
    /// nothing is picked. Always null on a single-tool printer, which has nothing to name.
    /// </summary>
    /// <remarks>
    /// <b>1-based.</b> Gcode's own <c>T</c> is 0-based, so a caller composing one subtracts - see
    /// <c>notes/toolchangers.md</c> §2, where getting that backwards unloads the next tool along.
    /// </remarks>
    public int? PickedTool { get; }

    /// <summary>Whether a toolless command will reach a hotend at all.</summary>
    /// <remarks>
    /// True on any single-tool printer, and on a multi-tool printer only while a tool is picked.
    /// <b>The one thing a caller should branch on</b>; the properties above are for wording an
    /// answer, not for deciding.
    /// </remarks>
    public bool ReachesAHotend => !IsMultiTool || PickedTool is not null;

    /// <summary>A printer with one tool: toolless commands act on it, and there is nothing to choose.</summary>
    public static ToolTarget SingleTool { get; } = new(isMultiTool: false, pickedTool: null);

    /// <summary>A multi-tool printer with nothing picked - the case this type exists for.</summary>
    public static ToolTarget NothingPicked { get; } = new(isMultiTool: true, pickedTool: null);

    /// <summary>A multi-tool printer with <paramref name="toolNumber"/> picked, 1-based.</summary>
    public static ToolTarget Picked(int toolNumber)
    {
        return new ToolTarget(isMultiTool: true, pickedTool: toolNumber);
    }

    /// <summary>
    /// Reads the situation from what a printer has reported.
    /// </summary>
    /// <param name="activeSlot">
    /// <see cref="Homespool.Model.Entities.PrinterLiveState.ActiveSlot"/>. Null when no slot block
    /// has arrived, which firmware sends only when <c>enabled_tool_cnt() &gt; 1</c>.
    /// </param>
    /// <param name="reportedToolCount">
    /// How many tools the printer described in its <c>INFO</c>. Unlike the slot block this is sent
    /// by every printer, one entry per enabled tool, so it counts rather than merely existing - and
    /// it is what separates "one tool" from "several, but no telemetry yet".
    /// </param>
    /// <remarks>
    /// <b>Unknown resolves to <see cref="NothingPicked"/>, not to <see cref="SingleTool"/>.</b> A
    /// printer that says it has several tools and has not yet said which is picked is exactly the
    /// state that must not be acted on, and defaulting the other way would act on it.
    /// </remarks>
    public static ToolTarget For(int? activeSlot, int reportedToolCount)
    {
        if (activeSlot is { } active)
        {
            return active <= 0 ? NothingPicked : Picked(active);
        }

        return reportedToolCount > 1 ? NothingPicked : SingleTool;
    }
}
