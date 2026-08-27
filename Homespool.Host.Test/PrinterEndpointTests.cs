using AwesomeAssertions;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

/// <summary>
/// Which endpoint a bundle points a printer at, and what follows from that choice.
/// </summary>
/// <remarks>
/// The escape hatch for firmware that cannot load a custom certificate. These assert the parts a
/// reader would otherwise have to trust: that choosing it really does produce a plaintext ini, that
/// it names the legacy port rather than the TLS one, and that it cannot be chosen on a deployment
/// that never opened it.
/// </remarks>
public class PrinterEndpointTests
{
    private static PrusaConnectOptions Options(int? legacyPort = null)
    {
        return new()
        {
            PrinterHost = "printers.example.com",
            PrinterPort = 15443,
            PrinterTls = true,
            LegacyPrinterPort = legacyPort,
        };
    }

    /// <summary>
    /// <b>No legacy endpoint unless the deployment opened one.</b> Without this, a bundle could name a port
    /// nothing is listening on — which is the same silent, unexplainable failure at the printer that
    /// the whole hatch exists to route around.
    /// </summary>
    [Fact]
    public void ThereIsNoLegacyEndpointUnlessTheDeploymentOpenedOne()
    {
        PrinterEndpoint.Legacy(Options()).Should().BeNull();
    }

    [Fact]
    public void TheLegacyEndpointNamesItsOwnPortAndNeverUsesTls()
    {
        PrinterEndpoint endpoint = PrinterEndpoint.Legacy(Options(legacyPort: 15800))!.Value;

        endpoint.Port.Should().Be(15800);
        endpoint.Tls.Should().BeFalse("there is nothing listening for a handshake on that port");
        endpoint.CarriesATrustAnchor.Should().BeFalse();
    }

    [Fact]
    public void TheDefaultEndpointFollowsTheDeployment()
    {
        PrinterEndpoint endpoint = PrinterEndpoint.Default(Options(legacyPort: 15800));

        endpoint.Port.Should().Be(15443, "opening a legacy listener must not move every other printer onto it");
        endpoint.Tls.Should().BeTrue();
    }

    /// <summary>
    /// The ini is the whole mechanism — the printer does what this file says and nothing else — so
    /// these four lines are the feature.
    /// </summary>
    [Fact]
    public void ALegacyIniPointsAtThePlainPortAndVerifiesNothing()
    {
        string ini = ConnectIni.BuildSnippet(PrinterEndpoint.Legacy(Options(legacyPort: 15800))!.Value,
                                             "printers.example.com",
                                             "token123");

        ini.Should().Contain("port = 15800");
        ini.Should().Contain("tls = False");
        ini.Should().Contain("custom_cert = 0", "a printer that verifies nothing must not be told to try");
        ini.Should().Contain("token = token123");
    }

    /// <summary>
    /// And the ordinary one is untouched by the legacy listener existing.
    /// </summary>
    [Fact]
    public void ADefaultIniIsUnchangedByTheLegacyEndpointBeingOpen()
    {
        string ini = ConnectIni.BuildSnippet(PrinterEndpoint.Default(Options(legacyPort: 15800)),
                                             "printers.example.com",
                                             "token123");

        ini.Should().Contain("port = 15443").And.Contain("tls = True").And.Contain("custom_cert = 1");
    }

    /// <summary>
    /// <b>The push-back the bundle page makes, and the two states it must tell apart.</b> A printer
    /// that has never connected has told us nothing, and silence is not evidence that it is capable —
    /// so only a stated, capable version earns the stronger warning.
    /// </summary>
    [Theory]
    [InlineData("6.5.7", true)]
    [InlineData("6.4.2", true)]
    [InlineData("6.2.6", false)]
    [InlineData("6.5.3", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void OnlyAStatedCapableVersionArguesAgainstTheLegacyEndpoint(string? firmware, bool expected)
    {
        Pages.Printers.BundleOffer offer = new(
            PrinterId: 1,
            PrinterName: "Bench",
            Token: "t",
            Names: [],
            Snippet: string.Empty,
            TlsEnabled: true,
            LegacyPort: 15800,
            KnownFirmware: firmware);

        offer.FirmwareCouldUseTls.Should().Be(expected);
    }

    /// <summary>
    /// The choice is not offered on a deployment that is already wholly plaintext: two plain listeners is
    /// a question with one meaning and two answers.
    /// </summary>
    [Theory]
    [InlineData(true, 15800, true)]
    [InlineData(true, null, false)]
    [InlineData(false, 15800, false)]
    public void TheChoiceIsOfferedOnlyWhereItMeansSomething(bool tlsEnabled, int? legacyPort, bool expected)
    {
        Pages.Printers.BundleOffer offer = new(
            PrinterId: 1,
            PrinterName: "Bench",
            Token: "t",
            Names: [],
            Snippet: string.Empty,
            TlsEnabled: tlsEnabled,
            LegacyPort: legacyPort);

        offer.CanOfferLegacyEndpoint.Should().Be(expected);
    }
}
