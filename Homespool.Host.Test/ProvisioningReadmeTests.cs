using AwesomeAssertions;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

/// <summary>
/// The instructions that travel with a bundle.
/// </summary>
/// <remarks>
/// Asserted on because the zip is opened somewhere the web page is not - by someone standing at a
/// printer with a USB stick, who never saw it. Everything that decides whether that goes well has to
/// be in the file.
/// </remarks>
public class ProvisioningReadmeTests
{
    private static PrusaConnectOptions Options(bool tls = true) =>
        new() { PrinterHost = "printers.example.com", PrinterPort = 15443, PrinterTls = tls };

    /// <summary>
    /// The two things most likely to go wrong are in it: the files go at the root of the stick, and
    /// the printer loses Prusa Connect until <c>custom_cert</c> is put back.
    /// </summary>
    [Fact]
    public void ItCarriesTheTwoWarningsThatMatterAtThePrinter()
    {
        string readme = ProvisioningReadme.Build(Options(), "printers.example.com", "Bench printer");

        readme.Should().Contain("top level").And.Contain("not into a folder");
        readme.Should().Contain("cannot use Prusa Connect").And.Contain("custom_cert = 0");
    }

    /// <summary>
    /// It says where this bundle points, since a folder of them is otherwise indistinguishable.
    /// </summary>
    [Fact]
    public void ItNamesThePrinterAndTheAddress()
    {
        string readme = ProvisioningReadme.Build(Options(), "192.168.13.238", "Bench printer");

        readme.Should().Contain("Bench printer")
            .And.Contain("192.168.13.238")
            .And.Contain("15443");
    }

    /// <summary>
    /// The transport is named in words people meet elsewhere, and tied back to the line in the ini
    /// that sets it.
    /// </summary>
    /// <remarks>
    /// "Encrypted: yes" was the first version and told a reader nothing they could act on - not what
    /// the setting is called, not where it lives, not what to search for when it goes wrong.
    /// </remarks>
    [Theory]
    [InlineData(true, "TLS", "HTTPS", "tls = True")]
    [InlineData(false, "plain HTTP", "not encrypted", "tls = False")]
    public void ItNamesTheTransportInTermsPeopleKnow(bool tls, string first, string second, string iniLine)
    {
        string readme = ProvisioningReadme.Build(Options(tls), "192.168.13.238", "Bench printer");

        readme.Should().Contain(first).And.Contain(second).And.Contain(iniLine);
        readme.Should().NotContain("| encrypted |", "that row said nothing a reader could use");
    }

    /// <summary>An unnamed printer reads as a printer, not as an empty gap.</summary>
    [Fact]
    public void AnUnnamedPrinterStillReadsProperly()
    {
        ProvisioningReadme.Build(Options(), "192.168.13.238", printerName: null)
            .Should().Contain("for a printer").And.NotContain("****");
    }

    /// <summary>
    /// Without TLS it explains the missing certificate rather than leaving a reader hunting for it,
    /// and does not claim a trust store was replaced when none was.
    /// </summary>
    [Fact]
    public void WithoutTlsItExplainsTheAbsentCertificate()
    {
        string readme = ProvisioningReadme.Build(Options(tls: false), "192.168.13.238", "Bench printer");

        readme.Should().Contain("no certificate").And.Contain("clear text");

        // It still names connect.der - to say why there isn't one. What it must not do is list it as a
        // file to look for in a zip that has none.
        readme.Should().NotContain("| `connect.der` |");
        readme.Should().NotContain("cannot use Prusa Connect");
    }
}
