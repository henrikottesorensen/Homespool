using AwesomeAssertions;

using Homespool.Host.Printing;

namespace Homespool.Host.Test;

/// <summary>
/// Which tool a toolless gcode command would reach, read from what a printer reported.
/// </summary>
/// <remarks>
/// <para>
/// <b>The distinction being made is <em>picked</em> versus <em>unpicked</em>, and a null check cannot
/// make it.</b> Firmware packs "no tool picked" into the slot block's <c>active</c> field as
/// <b><c>0</c></b>, a sentinel inside a number, while <em>absent</em> means something else entirely -
/// a single-tool printer, whose slot block firmware never sends. Two different unknowns, one of which
/// is safe to act on and one of which is not.
/// </para>
/// <para>
/// The consequence of getting it wrong is quiet: <c>M104</c> and <c>M702</c> decline to act and
/// report to the serial console, while the frame is answered <c>Accepted</c>. For preheat it is
/// worse, because <c>M140</c> has no tool and lands anyway.
/// </para>
/// </remarks>
public class ToolTargetTests
{
    /// <summary>A single-tool printer sends no slot block, and its one tool is always the target.</summary>
    [Fact]
    public void NoSlotBlockAndOneToolIsASingleToolPrinter()
    {
        ToolTarget target = ToolTarget.For(activeSlot: null, reportedToolCount: 1);

        target.IsMultiTool.Should().BeFalse();
        target.PickedTool.Should().BeNull("a single-tool printer has nothing to name");
        target.ReachesAHotend.Should().BeTrue();
    }

    /// <summary>
    /// Zero is firmware's "nothing picked", not an absent value - the case this type exists for.
    /// </summary>
    [Fact]
    public void ZeroMeansNothingIsPicked()
    {
        ToolTarget target = ToolTarget.For(activeSlot: 0, reportedToolCount: 5);

        target.IsMultiTool.Should().BeTrue();
        target.PickedTool.Should().BeNull();
        target.ReachesAHotend.Should().BeFalse("a toolless command would decline and say so only on serial");
    }

    /// <summary>A picked tool is named, and the number stays as the wire gave it - 1-based.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    public void APickedToolIsCarriedThroughOneBased(int active)
    {
        ToolTarget target = ToolTarget.For(activeSlot: active, reportedToolCount: 8);

        target.IsMultiTool.Should().BeTrue();
        target.PickedTool.Should().Be(active, "gcode's T is 0-based and the wire's slot number is not; "
                                              + "converting is the caller's job, and doing it twice unloads the wrong tool");
        target.ReachesAHotend.Should().BeTrue();
    }

    /// <summary>
    /// A printer that says it has several tools but has not said which is picked must not be acted on.
    /// </summary>
    /// <remarks>
    /// <b>The direction of this default is the whole point.</b> Resolving unknown to "single tool"
    /// would treat a toolchanger mid-connection as safe, which is exactly backwards: the slot block
    /// has simply not arrived yet, and acting would reach whatever firmware picks.
    /// </remarks>
    [Fact]
    public void SeveralToolsWithNothingReportedYetIsNotSafeToActOn()
    {
        ToolTarget target = ToolTarget.For(activeSlot: null, reportedToolCount: 5);

        target.IsMultiTool.Should().BeTrue();
        target.ReachesAHotend.Should().BeFalse("unknown resolves to nothing-picked, never to single-tool");
    }

    /// <summary>
    /// A printer that has reported nothing at all reads as single-tool, and the state guard is what
    /// covers it.
    /// </summary>
    /// <remarks>
    /// There is no third answer available here - a printer with no <c>INFO</c> and no telemetry is
    /// indistinguishable from a single-tool one on this evidence. It is also <c>Unknown</c>, which
    /// <see cref="PhysicalChangeRules"/> refuses, so nothing reaches a hotend on its say-so.
    /// </remarks>
    [Fact]
    public void APrinterThatHasReportedNothingReadsAsSingleTool()
    {
        ToolTarget.For(activeSlot: null, reportedToolCount: 0).IsMultiTool.Should().BeFalse();
    }
}
