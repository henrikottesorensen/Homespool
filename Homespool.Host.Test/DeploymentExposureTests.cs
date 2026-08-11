using System.Collections.Generic;
using System.Net;

using AwesomeAssertions;

using Homespool.Host.Services;

namespace Homespool.Host.Test;

/// <summary>
/// Whether this deployment is giving its credentials away, and to whom.
/// </summary>
/// <remarks>
/// Two questions with one thing in common: a credential readable by anyone on the path. A printer
/// token identifies a printer permanently; a session cookie is an administrator. Neither is worth a
/// banner for being unusual, and both are worth one for being legible.
/// </remarks>
public class DeploymentExposureTests
{
    private static readonly IReadOnlyList<IPNetwork> ContainerNetworks = [IPNetwork.Parse("172.16.0.0/12")];

    private static IReadOnlyList<IPAddress> At(params string[] addresses)
    {
        return [.. System.Linq.Enumerable.Select(addresses, IPAddress.Parse)];
    }

    /// <summary>
    /// The case the warning exists for: plaintext, at an address the internet can reach.
    /// </summary>
    [Fact]
    public void PlaintextAtAPublicAddressIsReported()
    {
        ExposureVerdict verdict = DeploymentExposure.EvaluatePrinterTransport(
            printerTls: false, "printers.example.com", At("203.0.113.9"), ContainerNetworks);

        verdict.State.Should().Be(ExposureState.PrinterTokensCrossThePublicInternet);
        verdict.Description.Should().Contain("printers.example.com")
               .And.Contain("203.0.113.9")
               .And.Contain("PrusaConnect:PrinterTls")
               .And.Contain("new provisioning bundles", "the ones already handed out say tls = False");
    }

    /// <summary>
    /// Plaintext on a private address is the supported testing mode and is left alone.
    /// </summary>
    /// <remarks>
    /// Startup already warns at every start. A permanent banner repeating it would be wallpaper, and
    /// then the case above - the one that genuinely needs shouting about - would look the same as the
    /// rig somebody is deliberately running.
    /// </remarks>
    [Theory]
    [InlineData("192.168.13.238")]
    [InlineData("10.1.2.3")]
    [InlineData("172.28.0.2")]
    [InlineData("127.0.0.1")]
    [InlineData("100.64.7.1")]
    public void PlaintextOnAPrivateAddressIsNotNaggedAbout(string address)
    {
        DeploymentExposure.EvaluatePrinterTransport(false, "homespool.lan", At(address), ContainerNetworks)
                          .IsProblem.Should().BeFalse();
    }

    /// <summary>One public answer among several is still public: that is the one an attacker uses.</summary>
    [Fact]
    public void AnyPublicAddressIsEnough()
    {
        DeploymentExposure.EvaluatePrinterTransport(
                              false, "homespool.lan", At("192.168.13.238", "203.0.113.9"), ContainerNetworks)
                          .State.Should().Be(ExposureState.PrinterTokensCrossThePublicInternet);
    }

    /// <summary>A name that resolves to nothing says nothing, so neither does this.</summary>
    [Fact]
    public void AnUnresolvableHostIsNotGuessedAt()
    {
        DeploymentExposure.EvaluatePrinterTransport(false, "homespool.lan", At(), ContainerNetworks)
                          .IsProblem.Should().BeFalse();
    }

    /// <summary>With TLS on there is nothing to report, whatever the address is.</summary>
    [Fact]
    public void TlsMakesTheAddressIrrelevant()
    {
        DeploymentExposure.EvaluatePrinterTransport(true, "printers.example.com", At("203.0.113.9"), ContainerNetworks)
                          .IsProblem.Should().BeFalse();
    }

    /// <summary>
    /// An administrator reading the site over plain HTTP from anywhere but this machine is told so.
    /// </summary>
    [Fact]
    public void APlaintextSessionFromTheNetworkIsReported()
    {
        ExposureVerdict verdict = DeploymentExposure.EvaluateAdminSession(
            isHttps: false, IPAddress.Parse("192.168.13.44"));

        verdict.State.Should().Be(ExposureState.SessionInClear);
        verdict.Description.Should().Contain("192.168.13.44")
               .And.Contain("session cookie")
               .And.Contain("Listeners:UserHttpsPort");
    }

    /// <summary>
    /// Loopback is exempt, and TLS is exempt - the second being the shipped stack, which must never
    /// see this banner.
    /// </summary>
    /// <remarks>
    /// The <c>isHttps</c> here is what the application sees <em>after</em> forwarded headers, so nginx
    /// terminating TLS and saying so leaves this silent. If that ever regressed, every page of the
    /// shipped deployment would carry a red banner - which is a good reason to pin it.
    /// </remarks>
    [Theory]
    [InlineData(true, "192.168.13.44")]
    [InlineData(false, "127.0.0.1")]
    [InlineData(true, "127.0.0.1")]
    public void EncryptedOrLocalSessionsAreLeftAlone(bool isHttps, string client)
    {
        DeploymentExposure.EvaluateAdminSession(isHttps, IPAddress.Parse(client))
                          .IsProblem.Should().BeFalse();
    }

    /// <summary>
    /// The address is written the way the reader's router writes it, not the way Kestrel stores it.
    /// </summary>
    /// <remarks>
    /// Kestrel reports IPv4 clients in mapped form, and <c>::ffff:192.168.13.44</c> in a sentence aimed
    /// at a person is something they have to decode before they can recognise their own network.
    /// </remarks>
    [Fact]
    public void TheClientAddressReadsAsAnAddressAPersonWouldRecognise()
    {
        ExposureVerdict verdict = DeploymentExposure.EvaluateAdminSession(
            isHttps: false, IPAddress.Parse("::ffff:192.168.13.44"));

        verdict.Description.Should().Contain("192.168.13.44").And.NotContain("::ffff:");
    }

    /// <summary>A request with no client address to speak of is not evidence of anything.</summary>
    [Fact]
    public void NoClientAddressIsNotReported()
    {
        DeploymentExposure.EvaluateAdminSession(isHttps: false, client: null).IsProblem.Should().BeFalse();
    }
}
