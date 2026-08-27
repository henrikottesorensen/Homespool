using AwesomeAssertions;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

/// <summary>
/// The custom-certificate capability table, boundary by boundary.
/// </summary>
/// <remarks>
/// <b>These cases exist to stop the predicate being simplified.</b> Every one of the broken releases
/// below is numerically above a working one, so any single comparison - <c>&gt;= 6.4.2</c> being the
/// tempting one - passes releases whose firmware never reads the certificate file. The fix was
/// cherry-picked per release branch, so the working set is genuinely discontiguous and the tests have
/// to say so.
/// </remarks>
public class PrinterFirmwareVersionTests
{
    [Theory]
    [InlineData("6.4.2")] // the fix release for its train, below two broken ones
    [InlineData("6.5.7")]
    [InlineData("6.6.0")]
    [InlineData("6.6.3")]
    [InlineData("6.8.1")]
    [InlineData("6.9.0")]
    [InlineData("6.5.7+12345")] // build metadata is not part of the question
    public void FirmwareThatCanLoadACustomCertificateSaysSo(string stated)
    {
        PrinterFirmwareVersion.CanLoadCustomCertificate(stated).Should().BeTrue();
    }

    [Theory]
    [InlineData("6.4.0")]
    [InlineData("6.4.1")] // one below the fix, and shares its tag date
    [InlineData("6.5.1")] // ABOVE 6.4.2 and still broken - the reason this is not a floor
    [InlineData("6.5.2")]
    [InlineData("6.5.3")]
    [InlineData("6.2.6")]
    [InlineData("6.3.4")]
    [InlineData("6.4.0+11974")]
    public void FirmwareThatCannotLoadACustomCertificateSaysSo(string stated)
    {
        PrinterFirmwareVersion.CanLoadCustomCertificate(stated).Should().BeFalse();
    }

    /// <summary>
    /// The single assertion that a floor would fail, stated on its own so the failure names the
    /// reason rather than one row of a table.
    /// </summary>
    [Fact]
    public void ABrokenReleaseAboveTheFixedOneIsStillBroken()
    {
        PrinterFirmwareVersion.CanLoadCustomCertificate("6.4.2").Should().BeTrue();
        PrinterFirmwareVersion.CanLoadCustomCertificate("6.5.3").Should()
                              .BeFalse("6.5.3 is numerically above 6.4.2 and predates the fix on its own branch, "
                                       + "so any single >= comparison is wrong here");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("6.5")]
    [InlineData("6.5.x")]
    [InlineData("-1.0.0")]
    public void AVersionThatCannotBeReadIsNeverCalledCapable(string? stated)
    {
        PrinterFirmwareVersion.CanLoadCustomCertificate(stated).Should()
                              .BeFalse("a printer we cannot place must never be told it is fine");
    }

    /// <summary>
    /// <b>A future major is assumed to have kept the fix</b> (Henrik, 2026-08-27), because the error
    /// only runs one way: this predicate warns and never gates, so calling a later release incapable
    /// buys silence exactly where the advice would have been right, while calling it capable costs a
    /// dismissible confirmation.
    /// </summary>
    [Theory]
    [InlineData("7.0.0")]
    [InlineData("7.4.1")]
    [InlineData("8.0.0")]
    public void AMajorAboveTheSixSeriesIsAssumedToHaveKeptTheFix(string stated)
    {
        PrinterFirmwareVersion.CanLoadCustomCertificate(stated).Should().BeTrue();
    }

    /// <summary>
    /// Below the series that introduced <c>custom_cert</c> there is nothing to load.
    /// </summary>
    [Theory]
    [InlineData("5.1.0")]
    [InlineData("4.3.4")]
    public void AMajorBelowTheSixSeriesHasNoCustomCertificateAtAll(string stated)
    {
        PrinterFirmwareVersion.CanLoadCustomCertificate(stated).Should().BeFalse();
    }

    [Fact]
    public void TheBuildMetadataIsDiscardedRatherThanParsed()
    {
        PrinterFirmwareVersion.TryParse("6.4.0+11974", out PrinterFirmwareVersion version).Should().BeTrue();

        version.Major.Should().Be(6);
        version.Minor.Should().Be(4);
        version.Patch.Should().Be(0);
    }
}
