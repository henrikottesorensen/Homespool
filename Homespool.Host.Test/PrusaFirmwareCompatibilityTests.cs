using AwesomeAssertions;

using Homespool.Host.Firmware;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrusaFirmwareCompatibility"/> - which reported models a build serves, as firmware's own
/// tables say.
/// </summary>
public sealed class PrusaFirmwareCompatibilityTests
{
    [Theory]
    [InlineData(1, 4, 0, "1.4.0", true)]
    [InlineData(1, 4, 0, "1.4.1", true)]
    [InlineData(1, 4, 0, "1.3.9", true)]
    [InlineData(1, 4, 0, "1.3.10", true)]
    [InlineData(1, 4, 0, "1.3.5", false)]
    [InlineData(1, 3, 5, "1.3.5", true)]
    [InlineData(1, 3, 5, "1.3.6", true)]
    [InlineData(1, 3, 5, "1.4.0", false)]
    [InlineData(3, 1, 0, "3.1.1", true)]
    [InlineData(7, 1, 0, "7.1.0", true)]
    [InlineData(7, 1, 0, "7.2.0", true)]
    [InlineData(7, 1, 0, "7.10.0", false)]
    [InlineData(7, 1, 0, "8.1.0", false)]
    [InlineData(7, 10, 0, "7.10.0", true)]
    [InlineData(7, 10, 0, "7.1.0", false)]
    [InlineData(2, 1, 0, "2.1.0", true)]
    [InlineData(2, 1, 0, "2.1.1", false)]
    public void ABuildFitsTheModelsItServes(int type, int version, int subversion, string printerType, bool fits)
    {
        // Arrange
        PrusaFirmwareHeader header = Header(type, version, subversion);

        // Act
        bool result = PrusaFirmwareCompatibility.Fits(header, printerType);

        // Assert
        result.Should().Be(fits);
    }

    [Fact]
    public void APrinterThatHasNotSaidWhatItIsFitsNothing()
    {
        PrusaFirmwareCompatibility.Fits(Header(7, 1, 0), printerType: null).Should().BeFalse();
    }

    [Fact]
    public void TheBuildIsTheHeadersTripleInThePrintersOrder()
    {
        PrusaFirmwareCompatibility.BuildOf(Header(1, 3, 5)).Should().Be("1.3.5");
    }

    private static PrusaFirmwareHeader Header(int type, int version, int subversion)
    {
        return new PrusaFirmwareHeader(Major: 7,
                                       Minor: 0,
                                       Patch: 0,
                                       Build: 1,
                                       Prerelease: string.Empty,
                                       Board: 0,
                                       PrinterType: type,
                                       PrinterVersion: version,
                                       PrinterSubversion: subversion,
                                       FirmwareLength: 0);
    }
}
