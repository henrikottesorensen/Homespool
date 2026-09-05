using System;
using System.Collections.Generic;

using AwesomeAssertions;

using Homespool.Host.Certificates;

namespace Homespool.Host.Test;

/// <summary>
/// The drift rules themselves, with no certificate, no filesystem and no machine involved.
/// </summary>
/// <remarks>
/// <para>
/// <c>PrinterCertificateHealthCheckTests</c> covers the same rules through real certificates, which is
/// what proves the gathering half works. This file covers the cases a machine cannot be asked to
/// produce - above all <b>"every address the certificate names has gone"</b>, which for real means
/// moving a DHCP lease and waiting.
/// </para>
/// <para>
/// That rule is the one the whole feature exists for, and before this split it was the one rule with
/// no test at all.
/// </para>
/// </remarks>
public class PrinterCertificateDriftTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    private static PrinterCertificateVerdict Evaluate(IReadOnlyList<string> covered,
                                                      IReadOnlyList<string> current,
                                                      string? configuredHost = null,
                                                      int leafDays = 400,
                                                      int authorityDays = 5000,
                                                      bool loopbackOnly = false)
    {
        return PrinterCertificateDrift.Evaluate(
            tlsEnabled: true,
            configuredHost,
            covered,
            current,
            Now.AddDays(leafDays),
            Now.AddDays(authorityDays),
            Now,
            loopbackOnly);
    }

    /// <summary>
    /// <b>The case this feature exists for.</b> The certificate names one address, the machine now has
    /// another, and every printer provisioned against the old one has silently stopped being able to
    /// verify this server.
    /// </summary>
    [Fact]
    public void EveryCoveredAddressHavingGoneIsDrift()
    {
        // Act - a lease that moved, with nothing configured to pin the name.
        PrinterCertificateVerdict verdict = Evaluate(covered: ["192.168.13.238"], current: ["192.168.13.99"]);

        // Assert
        verdict.State.Should().Be(PrinterCertificateState.AddressesMoved);
        verdict.IsProblem.Should().BeTrue();
        verdict.Description.Should().Contain("192.168.13.238").And.Contain("192.168.13.99")
               .And.Contain("Admin -> Printer certificate");
    }

    /// <summary>
    /// One surviving address is enough, which is the entire point of covering every plausible name at
    /// issue time.
    /// </summary>
    [Fact]
    public void OneSurvivingAddressIsNotDrift()
    {
        Evaluate(covered: ["homespool.lan", "192.168.13.238"], current: ["homespool.lan", "192.168.13.99"])
            .State.Should().Be(PrinterCertificateState.Ok);
    }

    /// <summary>
    /// Addresses this machine has gained are not drift. A VPN, a container bridge or a second
    /// interface appears routinely, and warning about each is how a banner stops being read.
    /// </summary>
    [Fact]
    public void ExtraAddressesAreNotDrift()
    {
        Evaluate(covered: ["192.168.13.238"], current: ["192.168.13.238", "10.8.0.2", "172.17.0.1"])
            .State.Should().Be(PrinterCertificateState.Ok);
    }

    /// <summary>
    /// A machine that can currently see nothing at all is not evidence that the certificate is wrong.
    /// </summary>
    /// <remarks>
    /// Detection returning nothing means the network is down or unreadable, not that the addresses
    /// moved - and a server that shouted "reissue!" every time an interface flapped would be teaching
    /// people to reissue for no reason, which costs a restart each time.
    /// </remarks>
    [Fact]
    public void DetectingNothingIsNotTreatedAsDrift()
    {
        Evaluate(covered: ["192.168.13.238"], current: []).State.Should().Be(PrinterCertificateState.Ok);
    }

    /// <summary>
    /// The configured address outranks the detected ones: it is what every bundle is written for.
    /// </summary>
    [Fact]
    public void AConfiguredAddressOutsideTheCertificateWinsOverEverythingElse()
    {
        // Act - the machine's detected address is covered, but the configured one is not.
        PrinterCertificateVerdict verdict = Evaluate(
            covered: ["192.168.13.238"], current: ["192.168.13.238"], configuredHost: "homespool.lan");

        // Assert
        verdict.State.Should().Be(PrinterCertificateState.ConfiguredAddressUncovered);
        verdict.Description.Should().Contain("homespool.lan").And.Contain("No provisioning bundle");
    }

    /// <summary>
    /// A configured address the certificate covers is healthy however much else has changed around it
    /// - a name outlives a lease, which is why it was worth configuring.
    /// </summary>
    [Fact]
    public void ACoveredConfiguredAddressSurvivesTheAddressesMoving()
    {
        Evaluate(covered: ["homespool.lan", "192.168.13.238"], current: ["192.168.13.99"], configuredHost: "homespool.lan")
            .State.Should().Be(PrinterCertificateState.Ok);
    }

    /// <summary>Expiries are reported once the addresses are known to be fine.</summary>
    [Theory]
    [InlineData(20, 5000, PrinterCertificateState.LeafExpiring)]
    [InlineData(400, 200, PrinterCertificateState.AuthorityExpiring)]
    [InlineData(400, 5000, PrinterCertificateState.Ok)]
    public void ExpiriesAreWarnedAboutInTime(int leafDays, int authorityDays, PrinterCertificateState expected)
    {
        Evaluate(covered: ["a.lan"], current: ["a.lan"], leafDays: leafDays, authorityDays: authorityDays)
            .State.Should().Be(expected);
    }

    /// <summary>
    /// Drift is reported ahead of expiry when both are true, because an address nobody can verify is
    /// broken now and an expiry is a date in the diary.
    /// </summary>
    [Fact]
    public void DriftOutranksAnApproachingExpiry()
    {
        Evaluate(covered: ["192.168.13.238"], current: ["192.168.13.99"], leafDays: 3)
            .State.Should().Be(PrinterCertificateState.AddressesMoved);
    }

    /// <summary>Plain HTTP has no certificate to be wrong about, and is not a problem to report.</summary>
    [Fact]
    public void PlaintextIsNotAProblem()
    {
        PrinterCertificateVerdict verdict = PrinterCertificateDrift.Evaluate(
            tlsEnabled: false, null, [], [], null, null, Now);

        verdict.State.Should().Be(PrinterCertificateState.NotInUse);
        verdict.IsProblem.Should().BeFalse();
    }

    /// <summary>
    /// A configured host of 21 characters is the first thing reported, even when the certificate
    /// covers it — the printer dials the first 20 and never connects.
    /// </summary>
    /// <remarks>
    /// The fixture is the shape of the name that took two printers offline: 21 characters, one over
    /// the firmware's field. The message has to name what the printer would actually dial, because
    /// that is the only thing that makes "connection error" on the panel make sense.
    /// </remarks>
    [Fact]
    public void ATwentyOneCharacterHostIsRefusedAheadOfEverythingElse()
    {
        PrinterCertificateVerdict verdict = Evaluate(
            covered: ["homespool.example.net"], current: ["homespool.example.net"], configuredHost: "homespool.example.net");

        verdict.State.Should().Be(PrinterCertificateState.ConfiguredAddressTooLong);
        verdict.IsProblem.Should().BeTrue();
        verdict.Description.Should().Contain("20-character")
               .And.Contain("homespool.example.ne", "it has to say what the printer would dial")
               .And.Contain("PRINTER_HOST");
    }

    /// <summary>Twenty characters is the limit, not one under it.</summary>
    [Fact]
    public void ATwentyCharacterHostIsAccepted()
    {
        PrinterCertificateVerdict verdict = Evaluate(
            covered: ["printers.example.net"], current: ["printers.example.net"], configuredHost: "printers.example.net");

        verdict.State.Should().Be(PrinterCertificateState.Ok);
    }

    /// <summary>
    /// The length rule bites on plain HTTP too, where every other rule stands down: the field is the
    /// printer's, and it truncates whatever the transport.
    /// </summary>
    [Fact]
    public void ATooLongHostIsReportedEvenOnPlaintext()
    {
        PrinterCertificateVerdict verdict = PrinterCertificateDrift.Evaluate(
            tlsEnabled: false, "homespool.example.net", [], [], null, null, Now);

        verdict.State.Should().Be(PrinterCertificateState.ConfiguredAddressTooLong);
    }

    /// <summary>
    /// A configured host that resolves only to loopback from inside the container is named as such,
    /// with the line to fix — rather than surfacing as a page that offers to drop working names.
    /// </summary>
    [Fact]
    public void AConfiguredHostResolvingOnlyToLoopbackIsNamed()
    {
        PrinterCertificateVerdict verdict = Evaluate(
            covered: ["homespool.lan", "192.168.13.108"], current: ["homespool.lan"], configuredHost: "homespool.lan",
            loopbackOnly: true);

        verdict.State.Should().Be(PrinterCertificateState.ConfiguredAddressResolvesToLoopback);
        verdict.IsProblem.Should().BeTrue();
        verdict.Description.Should().Contain("homespool.lan").And.Contain("127.0.1.1").And.Contain("/etc/hosts");
    }

    /// <summary>
    /// An uncovered configured address still comes first: it stops provisioning today, while the
    /// loopback answer only misleads.
    /// </summary>
    [Fact]
    public void AnUncoveredAddressOutranksTheLoopbackAnswer()
    {
        PrinterCertificateVerdict verdict = Evaluate(
            covered: ["192.168.13.108"], current: ["homespool.lan"], configuredHost: "homespool.lan", loopbackOnly: true);

        verdict.State.Should().Be(PrinterCertificateState.ConfiguredAddressUncovered);
    }
}
