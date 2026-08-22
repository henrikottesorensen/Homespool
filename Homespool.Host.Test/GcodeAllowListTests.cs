using System;
using System.Linq;

using AwesomeAssertions;

using Homespool.Host.PrusaConnect.Commands;

namespace Homespool.Host.Test;

/// <summary>
/// The allowlist that decides which gcode this application is able to emit at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>The refusals are the tests that matter.</b> A permissive matcher passes every positive case
/// here and every other test in the suite, so the negatives below are the only thing standing
/// between this and the chain <c>GCode</c> documents: firmware's <c>M997</c> reflashes the mainboard
/// from a file on the USB stick, validating nothing, so "upload a file" plus "send arbitrary gcode"
/// is arbitrary firmware on somebody's printer.
/// </para>
/// <para>
/// The smuggling cases are written as they are because prefix matching is how allowlists like this
/// fail in practice, not because any caller sends them - nothing accepts gcode from a user today.
/// They guard the shape of the check rather than a live input.
/// </para>
/// </remarks>
public class GcodeAllowListTests
{
    [Theory]
    [InlineData("M104 S215")]
    [InlineData("M104 S0")]
    [InlineData("M104 S300")]
    [InlineData("M140 S60")]
    [InlineData("M140 S0")]
    [InlineData("M140 S120")]
    [InlineData("M702 W0")]
    public void TheLinesThisApplicationComposesArePermitted(string line)
    {
        GcodeAllowList.IsAllowed(line).Should().BeTrue();
    }

    /// <summary>
    /// Unloading is permitted in exactly one form, and every neighbouring form is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The arguments are not a range, they are the difference between headless and stuck.</b>
    /// <c>W</c> chooses which optional items firmware's preheat dialog offers, so <c>M702 W1</c>,
    /// <c>W2</c> and <c>W3</c> put menu entries on a panel nobody is standing at; <c>I</c> adds a
    /// confirmation prompt; and bare <c>M702</c> means <c>W255</c>, which is <i>do not preheat</i>
    /// and would try to pull filament through a cold nozzle.
    /// </para>
    /// <para>
    /// So this is the case where a range-checked pattern would have been wrong. Written out rather
    /// than reasoned about, because "M702 with a W argument" is the shape somebody would naturally
    /// relax this to.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("M702")]
    [InlineData("M702 W255")]
    [InlineData("M702 W1")]
    [InlineData("M702 W2")]
    [InlineData("M702 W3")]
    [InlineData("M702 W0 I")]
    [InlineData("M702 I")]
    [InlineData("M702 W0 T1")]
    [InlineData("M7020 W0")]
    [InlineData("M702W0")]
    [InlineData("m702 w0")]
    public void EveryUnloadFormOtherThanTheHeadlessOneIsRefused(string line)
    {
        GcodeAllowList.IsAllowed(line).Should().BeFalse();
    }

    /// <summary>Surrounding whitespace is the one thing normalised, since it can hide nothing.</summary>
    [Theory]
    [InlineData(" M104 S215")]
    [InlineData("M104 S215 ")]
    [InlineData("\tM140 S60\n")]
    public void SurroundingWhitespaceIsTolerated(string line)
    {
        GcodeAllowList.IsAllowed(line).Should().BeTrue();
    }

    /// <summary>
    /// The command this list exists to exclude, named rather than merely absent.
    /// </summary>
    /// <remarks>
    /// Absence alone would pass today. This asserts it by name so that widening the list in a hurry
    /// fails here with a message that says what was broken, instead of going quietly green.
    /// </remarks>
    [Theory]
    [InlineData("M997")]
    [InlineData("M997 S1")]
    [InlineData("m997")]
    public void TheReflashCommandIsRefused(string line)
    {
        GcodeAllowList.IsAllowed(line).Should().BeFalse(
            "M997 flashes firmware from a file on the USB stick and validates nothing");
    }

    /// <summary>
    /// A second command hidden on a permitted line. Each of these is how a prefix match or a
    /// normalising parser gets walked past.
    /// </summary>
    /// <remarks>
    /// The newline cases are the interesting ones now that a body may legitimately carry several
    /// lines: what makes these refusals is that <c>M997</c> is not permitted <em>on any line</em>,
    /// not that a separator is present.
    /// </remarks>
    [Theory]
    [InlineData("M104 S215 M997")]
    [InlineData("M104 S215\nM997")]
    [InlineData("M104 S215\r\nM997")]
    [InlineData("M104 S215 ; M997")]
    [InlineData("M104 S215*42")]
    [InlineData("N1 M104 S215")]
    [InlineData("M104 S215 (M997)")]
    public void ASecondCommandOnThePermittedLineIsRefused(string line)
    {
        GcodeAllowList.IsAllowed(line).Should().BeFalse();
    }

    /// <summary>
    /// Several permitted lines in one body are permitted - which is how both heaters get set by one
    /// command, and therefore atomically.
    /// </summary>
    [Theory]
    [InlineData("M140 S85\nM104 S230")]
    [InlineData("M140 S0\nM104 S0")]
    [InlineData("M104 S215\nM140 S60")]
    public void SeveralPermittedLinesInOneBodyArePermitted(string body)
    {
        GcodeAllowList.IsAllowed(body).Should().BeTrue();
    }

    /// <summary>
    /// One bad line rejects the whole body. A frame is executed as a unit, so admitting it partly
    /// would mean admitting it entirely.
    /// </summary>
    [Theory]
    [InlineData("M140 S85\nM997")]
    [InlineData("M997\nM140 S85")]
    [InlineData("M140 S85\nM104 S999")]
    public void OneRefusedLineRefusesTheWholeBody(string body)
    {
        GcodeAllowList.IsAllowed(body).Should().BeFalse();
    }

    /// <summary>A body may not be a batch: more lines than the pair this exists for is refused.</summary>
    [Fact]
    public void MoreLinesThanTheCapIsRefused()
    {
        string body = string.Join("\n", Enumerable.Repeat("M104 S215", GcodeAllowList.MaxLines + 1));

        GcodeAllowList.IsAllowed(body).Should().BeFalse();
    }

    /// <summary>
    /// Near-misses on the code itself: a permitted prefix does not make a permitted command.
    /// </summary>
    [Theory]
    [InlineData("M1040 S215")]
    [InlineData("M10 S215")]
    [InlineData("M1400 S60")]
    [InlineData("m104 S215")]
    [InlineData("M104S215")]
    [InlineData("M104  S215")]
    public void ALineThatMerelyLooksLikeOneIsRefused(string line)
    {
        GcodeAllowList.IsAllowed(line).Should().BeFalse();
    }

    /// <summary>
    /// Out-of-range temperatures are refused here rather than sent for the printer to reject - the
    /// shape being right does not make the value sane.
    /// </summary>
    [Theory]
    [InlineData("M104 S301")]
    [InlineData("M104 S999")]
    [InlineData("M140 S121")]
    public void ATemperatureAboveTheCeilingIsRefused(string line)
    {
        GcodeAllowList.IsAllowed(line).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingIsNotALine(string? line)
    {
        GcodeAllowList.IsAllowed(line).Should().BeFalse();
    }

    /// <summary>
    /// Every typed command's own line is one this list permits.
    /// </summary>
    /// <remarks>
    /// <b>The coupling that would otherwise break silently.</b> A command composing a line the list
    /// refuses does not fail at compile time and does not fail here in isolation - it fails at the
    /// encoder, at the moment somebody presses the button, on a printer. Asserting the two agree
    /// keeps that discovery in the suite.
    /// </remarks>
    [Fact]
    public void TheTypedCommandsComposeLinesThisListPermits()
    {
        GcodeAllowList.IsAllowed(new UnloadFilament().Line).Should().BeTrue();
        GcodeAllowList.IsAllowed(new SetTemperatures(230, 85).Line).Should().BeTrue();
    }

    /// <summary>
    /// The encoder refuses too, so the check cannot be skipped by a new call path reaching past the
    /// typed commands.
    /// </summary>
    [Fact]
    public void TheEncoderRefusesALineOffTheList()
    {
        Action encode = () => CommandWireEncoder.Encode(1, new HostileGcodeCommand());

        encode.Should().Throw<ArgumentException>();
    }

    /// <summary>A command that composes a line nothing should be able to send.</summary>
    private sealed class HostileGcodeCommand : ISendableGcodeCommand
    {
        public string WireName => "GCODE";

        public string Line => GcodeAllowList.ReflashCommand;
    }
}
