using AwesomeAssertions;

using Homespool.Host.Pages;

namespace Homespool.Host.Test;

/// <summary>
/// The firmware version as the tiles, the listing's cards and the status card show it.
/// </summary>
public class PrinterFirmwareReleaseTests
{
    /// <summary>The build number is dropped; it names a build, not a release.</summary>
    [Fact]
    public void DropsTheBuildNumber()
    {
        PrinterFirmwareRelease.For("6.4.0+11974").Should().Be("6.4.0");
    }

    /// <summary>A version with no build number is shown as it came.</summary>
    [Fact]
    public void KeepsAVersionWithNoBuildNumber()
    {
        PrinterFirmwareRelease.For("6.5.7").Should().Be("6.5.7");
    }

    /// <summary>
    /// <b>A prerelease survives.</b> Parsing to a triple would refuse it, and somebody on a release
    /// candidate is exactly who wants to see that they are.
    /// </summary>
    [Fact]
    public void KeepsAPrerelease()
    {
        PrinterFirmwareRelease.For("7.0.0-RC1+12345").Should().Be("7.0.0-RC1");
    }

    /// <summary>Nothing stated is nothing shown, rather than an empty label.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SaysNothingBeforeThePrinterHas(string? stated)
    {
        PrinterFirmwareRelease.For(stated).Should().BeNull();
        PrinterFirmwareRelease.Tooltip(stated).Should().BeNull();
    }

    /// <summary>
    /// A string with nothing before the <c>+</c> is shown whole: it is still the printer's claim.
    /// </summary>
    [Fact]
    public void ShowsABuildNumberAloneRatherThanNothing()
    {
        PrinterFirmwareRelease.For("+11974").Should().Be("+11974");
    }

    /// <summary>The tooltip carries what the label cut, and only then.</summary>
    [Theory]
    [InlineData("6.4.0+11974", "6.4.0+11974")]
    [InlineData(" 6.4.0+11974 ", "6.4.0+11974")]
    [InlineData("6.5.7", null)]
    [InlineData("+11974", null)]
    public void TheTooltipIsTheWholeVersionWhenTheLabelIsNot(string stated, string? tooltip)
    {
        PrinterFirmwareRelease.Tooltip(stated).Should().Be(tooltip);
    }
}
