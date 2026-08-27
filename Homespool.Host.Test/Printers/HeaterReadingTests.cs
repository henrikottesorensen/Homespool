using AwesomeAssertions;

using Homespool.Host.Pages.Printers;

namespace Homespool.Host.Test.Printers;

/// <summary>
/// Turning two numbers into the sentence the status card puts under them.
/// </summary>
public sealed class HeaterReadingTests
{
    /// <summary>
    /// Zero is reserved, so nothing that was never computed can pass for a state a printer is in.
    /// </summary>
    /// <remarks>
    /// <b>Worth a test rather than trusting the enum's shape</b> - deleting the sentinel does not
    /// break a build, it silently makes zero mean whichever member moves up into it.
    /// </remarks>
    [Fact]
    public void TheDefaultStateIsNotARealOne()
    {
        default(HeaterState).Should().Be(HeaterState.Undefined);
    }

    /// <summary>
    /// And nothing reachable produces it: every pair of numbers a printer can send lands on a state
    /// that says something.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData(null, 215f)]
    [InlineData(20f, null)]
    [InlineData(20f, 0f)]
    [InlineData(215f, 215f)]
    [InlineData(100f, 215f)]
    [InlineData(215f, 60f)]
    public void ARealReadingIsNeverUndefined(float? current, float? target)
    {
        HeaterReading.For(current, target).State.Should().NotBe(HeaterState.Undefined);
    }

    /// <summary>
    /// A sensor the wire does not aim is never inferred from a pair of numbers - it is constructed,
    /// because a missing target legitimately means "off" for anything that is a heater.
    /// </summary>
    [Theory]
    [InlineData(25f, null)]
    [InlineData(60f, null)]
    [InlineData(45f, 0f)]
    public void AMeasuredSensorIsNeverInferred(float? current, float? target)
    {
        HeaterReading.For(current, target).State.Should().NotBe(HeaterState.Measured);
    }

    /// <summary>A heater nobody has heard from says nothing at all.</summary>
    [Fact]
    public void NoReadingIsUnknown()
    {
        HeaterReading.For(null, 215).State.Should().Be(HeaterState.Unknown);
    }

    /// <summary>Below its setpoint and climbing.</summary>
    [Fact]
    public void BelowTargetIsHeating()
    {
        HeaterReading.For(120, 215).State.Should().Be(HeaterState.Heating);
    }

    /// <summary>Above a live setpoint is coming down towards it, not off.</summary>
    [Fact]
    public void AboveALiveTargetIsCooling()
    {
        HeaterReading.For(230, 215).State.Should().Be(HeaterState.Cooling);
    }

    /// <summary>
    /// A real printer holding 215 reports either side of it once a second. Without a tolerance the
    /// card would flicker between two answers and be right in neither.
    /// </summary>
    [Theory]
    [InlineData(214.6f)]
    [InlineData(215f)]
    [InlineData(215.3f)]
    public void CloseEnoughIsAtTarget(float current)
    {
        HeaterReading.For(current, 215).State.Should().Be(HeaterState.AtTarget);
    }

    /// <summary>The tolerance is not a licence: a real gap is still heating.</summary>
    [Fact]
    public void JustOutsideTheToleranceIsNotAtTarget()
    {
        HeaterReading.For(215 - HeaterReading.Tolerance - 0.5f, 215).State.Should().Be(HeaterState.Heating);
    }

    /// <summary>
    /// Firmware reports a switched-off heater's setpoint as zero, which is also what a cooldown sets -
    /// so zero means off rather than "asked for zero degrees".
    /// </summary>
    [Fact]
    public void ZeroTargetIsNotATargetOfZero()
    {
        HeaterReading.For(210, 0).State.Should().Be(HeaterState.Cooling);
    }

    /// <summary>Off and cold, which is the resting state of every idle printer.</summary>
    [Fact]
    public void ColdWithNoTargetIsOff()
    {
        HeaterReading.For(22, 0).State.Should().Be(HeaterState.Off);
    }

    /// <summary>
    /// Off but still hot is worth saying: it is the difference between a machine you can reach into
    /// and one you cannot.
    /// </summary>
    [Fact]
    public void WarmWithNoTargetIsCooling()
    {
        HeaterReading.For(HeaterReading.WarmAbove + 5, null).State.Should().Be(HeaterState.Cooling);
    }
}
