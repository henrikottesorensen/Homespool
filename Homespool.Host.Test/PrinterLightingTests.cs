using AwesomeAssertions;

using Homespool.Host.Printing;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrinterLighting"/> - which printers have lighting to set, which of them say how bright
/// it is, and what they will say after being set.
/// </summary>
public sealed class PrinterLightingTests
{
    /// <summary>
    /// Firmware's <c>PRINTERS_WITH_SIDE_LEDS</c>, read by printer type: the XL and its XL+ build, and
    /// every CORE One - known before the printer has reported anything.
    /// </summary>
    [Theory]
    [InlineData("3.1.0")]
    [InlineData("3.1.1")]
    [InlineData("7.1.0")]
    [InlineData("8.1.0")]
    [InlineData("7.10.0")]
    [InlineData("8.10.0")]
    public void AModelBuiltWithTheStripsHasLighting(string printerType)
    {
        PrinterLighting.Has(printerType, reportedIntensity: null).Should().BeTrue();
    }

    /// <summary>
    /// A model built without the strips has none, which is what keeps a command firmware would
    /// answer "Missing or broken parameters" from being sent.
    /// </summary>
    [Theory]
    [InlineData("1.3.5")]
    [InlineData("1.4.0")]
    [InlineData("2.1.0")]
    [InlineData("4.1.0")]
    [InlineData(null)]
    [InlineData("99.9.9")]
    public void AModelBuiltWithoutTheStripsHasNoLighting(string? printerType)
    {
        PrinterLighting.Has(printerType, reportedIntensity: null).Should().BeFalse();
    }

    /// <summary>
    /// A reported brightness is proof on its own, whatever the model says - one this table does not
    /// know yet included.
    /// </summary>
    [Theory]
    [InlineData("99.9.9")]
    [InlineData(null)]
    public void AReportedBrightnessMeansLightingWhateverTheModel(string? printerType)
    {
        PrinterLighting.Has(printerType, reportedIntensity: 0).Should().BeTrue("a light reported off is still a light");
    }

    /// <summary>The XL builds take the setting and never report it; nothing else with strips is like that.</summary>
    [Theory]
    [InlineData("3.1.0", true)]
    [InlineData("3.1.1", true)]
    [InlineData("7.1.0", false)]
    [InlineData("8.1.0", false)]
    [InlineData("1.3.5", false)]
    public void OnlyTheXlNeverReportsItsBrightness(string printerType, bool unreported)
    {
        PrinterLighting.Unreported(printerType).Should().Be(unreported);
    }

    /// <summary>
    /// What the printer reports after being set: firmware's round trip through a byte, which loses a
    /// step at some values and keeps both ends.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(33, 32)]
    [InlineData(40, 40)]
    [InlineData(99, 98)]
    [InlineData(100, 100)]
    public void ReadBackIsFirmwaresRoundTrip(int set, int reported)
    {
        PrinterLighting.ReadBack(set).Should().Be(reported);
    }
}
