using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Homespool.Host.Certificates;

namespace Homespool.Host.Test;

/// <summary>
/// The certificate authority a provisioned printer trusts, and the leaf served on the printer
/// listener.
/// </summary>
/// <remarks>
/// Every property here is a firmware constraint rather than a preference, and each fails in a way
/// that looks like something else: an RSA key produces a handshake alert that reads like a printer
/// fault, and an RFC-correct IP SAN produces a name mismatch.
/// </remarks>
public sealed class PrinterCertificateAuthorityTests : IDisposable
{
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hs-ca-{Guid.NewGuid():N}");

    private static string[] DnsNames(X509Certificate2 certificate)
    {
        return certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>()
                          .SelectMany(e => e.EnumerateDnsNames())
                          .ToArray();
    }

    private PrinterCertificateAuthority NewAuthority()
    {
        return new(Options.Create(new CertificateOptions { Directory = "certs" }),
                   new HostEnvironmentAccessor(_root),
                   TimeProvider.System,
                   NullLogger<PrinterCertificateAuthority>.Instance);
    }

    /// <summary>
    /// Both the authority and the leaf use ECDSA on P-256.
    /// </summary>
    /// <remarks>
    /// The firmware compiles exactly one ciphersuite, <c>ECDHE-ECDSA-AES128-GCM-SHA256</c>, with one
    /// curve. An RSA certificate shares no ciphersuite with it, so the handshake dies before
    /// validation is even reached - reported to the screen as a bare "TLS error".
    /// </remarks>
    [Fact]
    public void TheAuthorityAndLeafAreEcdsaP256()
    {
        // Act
        PrinterCertificateAuthority authority = NewAuthority();
        using X509Certificate2 ca = authority.EnsureAuthority();
        using X509Certificate2 leaf = authority.IssueLeaf(["192.168.13.238"]);

        // Assert
        foreach (X509Certificate2 certificate in new[] { ca, leaf })
        {
            certificate.GetECDsaPublicKey().Should().NotBeNull("RSA cannot negotiate with this firmware");
            certificate.GetECDsaPublicKey()!.KeySize.Should().Be(256);
            certificate.GetRSAPublicKey().Should().BeNull();
        }
    }

    /// <summary>
    /// The PEM written for nginx carries the leaf and nothing else.
    /// </summary>
    /// <remarks>
    /// Firmware's <c>x509_crt_check_ee_locally_trusted</c> wants exactly one certificate presented.
    /// A terminator handed leaf + authority fails verification looking like a protocol fault rather
    /// than a certificate one - the shape of failure this project has spent whole afternoons on - so
    /// the file being single is asserted rather than assumed.
    /// </remarks>
    [Fact]
    public void TheNginxPemHoldsTheLeafAloneWithNoChain()
    {
        // Arrange
        PrinterCertificateAuthority authority = NewAuthority();

        // Act
        using X509Certificate2 leaf = authority.IssueLeaf(["192.168.13.238"]);

        string certificatePem = File.ReadAllText(authority.LeafCertificatePemPath);
        string keyPem = File.ReadAllText(authority.LeafKeyPemPath);

        // Assert
        Regex.Matches(certificatePem, "BEGIN CERTIFICATE").Count.Should().Be(1,
                                                                             "a second certificate here is the authority, and presenting it fails verification on the printer");

        X509Certificate2 fromPem = X509Certificate2.CreateFromPem(certificatePem);

        fromPem.Thumbprint.Should().Be(leaf.Thumbprint, "and it has to be the leaf that was just issued");
        keyPem.Should().Contain("BEGIN PRIVATE KEY", "nginx needs the key beside it");
        fromPem.Dispose();
    }

    /// <summary>
    /// The key nginx reads is group-readable and nothing more; the certificate beside it is public
    /// material and stays world-readable.
    /// </summary>
    /// <remarks>
    /// The group grant is the proxy's whole access - compose adds the app's group to the nginx
    /// container - so widening past it hands the key to any other uid that ever shares the volume,
    /// and narrowing to owner-only takes the printer listener down with a certificate error that has
    /// nothing to do with the certificate.
    /// </remarks>
    [Fact]
    public void TheProxyKeyIsGroupReadableAndNoWider()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Act
        PrinterCertificateAuthority authority = NewAuthority();
        authority.IssueLeaf(["192.168.13.238"]).Dispose();

        // Assert
        File.GetUnixFileMode(authority.LeafKeyPemPath)
            .Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        File.GetUnixFileMode(authority.LeafCertificatePemPath)
            .Should().HaveFlag(UnixFileMode.OtherRead, "the certificate is public material");
    }

    /// <summary>
    /// A key an earlier version wrote world-readable is tightened on the next start, because the leaf
    /// is deliberately never reissued and so the write path never touches it again.
    /// </summary>
    [Fact]
    public void AWorldReadableKeyFromAnEarlierVersionIsTightenedOnStartup()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange
        PrinterCertificateAuthority authority = NewAuthority();
        authority.EnsureLeaf(["192.168.13.238"]).Dispose();

        File.SetUnixFileMode(authority.LeafKeyPemPath,
                             UnixFileMode.UserRead | UnixFileMode.UserWrite
                                                   | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        // Act - a "restart" on a deployment whose leaf and PEMs already exist.
        NewAuthority().EnsureLeaf(["192.168.13.238"]).Dispose();

        // Assert
        File.GetUnixFileMode(authority.LeafKeyPemPath)
            .Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
    }

    /// <summary>
    /// A bare IP address is carried as a <c>dNSName</c> SAN, not as the RFC-correct <c>iPAddress</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single most counterintuitive thing in this class, and the one a future reader is most
    /// likely to "correct". mbedTLS matches SAN entries by plain string comparison and never parses
    /// them as addresses, so the literal in a dNSName entry matches. Its <c>iPAddress</c> handling
    /// reaches an "unrecognised type" arm and never matches at all - so the conformant encoding is
    /// precisely the one that fails.
    /// </para>
    /// <para>
    /// Confirmed on a real MK3.5 on 2026-07-29, which connected against exactly this shape. Switching
    /// to <c>AddIpAddress</c> would leave this test the only thing standing between the change and a
    /// fleet that cannot connect.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnIpAddressIsCarriedAsADnsNameSoTheFirmwareCanMatchIt()
    {
        // Act
        using X509Certificate2 leaf = NewAuthority().IssueLeaf(["192.168.13.238"]);

        // Assert
        DnsNames(leaf).Should().Contain("192.168.13.238");

        // And genuinely not as an iPAddress entry, which is what would silently break matching.
        leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(e => e.EnumerateIPAddresses())
            .Should().BeEmpty("firmware never matches iPAddress entries");
    }

    /// <summary>
    /// Every name given is present, so a hostname and an address can both keep working.
    /// </summary>
    /// <remarks>
    /// The hedge against the DHCP problem: the deployment's address is baked into the certificate, so
    /// carrying both names means a moved lease does not necessarily strand every printer.
    /// </remarks>
    [Fact]
    public void EveryNameGivenBecomesASubjectAlternativeName()
    {
        // Act
        using X509Certificate2 leaf = NewAuthority().IssueLeaf(["printer.example.com", "192.168.13.238"]);

        // Assert
        DnsNames(leaf).Should().BeEquivalentTo(["printer.example.com", "192.168.13.238"]);
    }

    /// <summary>
    /// The authority is a CA and the leaf is not.
    /// </summary>
    [Fact]
    public void BasicConstraintsDistinguishTheAuthorityFromTheLeaf()
    {
        // Act
        PrinterCertificateAuthority authority = NewAuthority();
        using X509Certificate2 ca = authority.EnsureAuthority();
        using X509Certificate2 leaf = authority.IssueLeaf(["homespool.lan"]);

        // Assert
        ca.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority.Should().BeTrue();
        leaf.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority.Should().BeFalse();
    }

    /// <summary>
    /// The leaf is usable as a server certificate and chains to the authority.
    /// </summary>
    /// <remarks>
    /// Chain building with the authority as the sole trust anchor is what the printer does, so this
    /// is the closest a unit test gets to the real check.
    /// </remarks>
    [Fact]
    public void TheLeafChainsToTheAuthorityAndIsForServerAuth()
    {
        // Arrange
        PrinterCertificateAuthority authority = NewAuthority();
        using X509Certificate2 ca = authority.EnsureAuthority();
        using X509Certificate2 leaf = authority.IssueLeaf(["homespool.lan"]);

        using X509Chain chain = new();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        // Assert
        chain.Build(leaf).Should().BeTrue(
            "the printer builds exactly this chain, with connect.der as its only anchor");

        leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .SelectMany(e => e.EnhancedKeyUsages.Cast<Oid>())
            .Select(o => o.Value)
            .Should().Contain(ServerAuthOid);
    }

    /// <summary>
    /// A second call reuses the authority rather than minting another.
    /// </summary>
    /// <remarks>
    /// <b>The most consequential test in this file.</b> A regenerated authority does not fail, log or
    /// look wrong - it silently stops every already-provisioned printer from validating this server,
    /// and because a <c>.der</c> cannot be sent over Connect, the only remedy is a USB visit to each
    /// one. Restarts are routine; this must survive them.
    /// </remarks>
    [Fact]
    public void TheAuthorityIsMintedOnceAndReusedAfterwards()
    {
        // Act
        using X509Certificate2 first = NewAuthority().EnsureAuthority();
        using X509Certificate2 second = NewAuthority().EnsureAuthority(); // a "restart"

        // Assert
        second.Thumbprint.Should().Be(first.Thumbprint,
                                      "a fresh authority would strand every printer provisioned from the previous one");
    }

    /// <summary>
    /// Reissuing a leaf does not disturb the authority.
    /// </summary>
    /// <remarks>
    /// This asymmetry is the whole reason a CA was chosen over a self-signed leaf: names can change
    /// without a trip to every printer.
    /// </remarks>
    [Fact]
    public void ReissuingALeafKeepsTheSameAuthority()
    {
        // Arrange
        PrinterCertificateAuthority authority = NewAuthority();
        using X509Certificate2 ca = authority.EnsureAuthority();

        // Act
        using X509Certificate2 first = authority.IssueLeaf(["one.lan"]);
        using X509Certificate2 second = authority.IssueLeaf(["two.lan"]);

        // Assert
        second.Thumbprint.Should().NotBe(first.Thumbprint);
        authority.EnsureAuthority().Thumbprint.Should().Be(ca.Thumbprint);
        DnsNames(second).Should().BeEquivalentTo(["two.lan"]);
    }

    /// <summary>
    /// <c>connect.der</c> is the authority, DER-encoded, carrying no private key.
    /// </summary>
    /// <remarks>
    /// This is the file that goes on the USB stick. PEM is unsupported by the firmware and a renamed
    /// file fails as <c>Error::Tls</c> with no explanation, so the encoding is not cosmetic. It must
    /// also be the public certificate alone - shipping the CA's private key to every printer would
    /// hand out the ability to mint certificates they trust.
    /// </remarks>
    [Fact]
    public void TheDerFileIsTheAuthorityWithoutItsPrivateKey()
    {
        // Arrange
        PrinterCertificateAuthority authority = NewAuthority();
        using X509Certificate2 ca = authority.EnsureAuthority();

        // Act
        byte[] der = File.ReadAllBytes(authority.AuthorityDerPath);
        using X509Certificate2 loaded = X509CertificateLoader.LoadCertificate(der);

        // Assert
        loaded.Thumbprint.Should().Be(ca.Thumbprint);
        loaded.HasPrivateKey.Should().BeFalse("this file is handed to every printer");
        der.Should().StartWith([(byte)0x30], "DER-encoded certificates begin with a SEQUENCE tag");
    }

    /// <summary>
    /// Both certificates are valid from before the epoch, so a printer with no clock still connects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Looks like a mistake and is not.</b> Buddy's <c>time()</c> returns <c>-1</c> when the RTC
    /// was never set, and mbedTLS does not treat that as an error - <c>gmtime_r(-1)</c> succeeds - so
    /// it believes the date is <b>1969-12-31</b> and every ordinary certificate is "not yet valid".
    /// With <c>MBEDTLS_SSL_VERIFY_REQUIRED</c> and no verify callback, the handshake just fails.
    /// </para>
    /// <para>
    /// Not an edge case: the MINI's Buddy board ties VBAT to +3.3V with no cell
    /// (<c>Buddy-board-MINI-PCB rev.1.0.0/cpu.sch</c>), so its clock is lost at <i>every</i>
    /// power-off. xBuddy boards carry a CR1220 and keep time - which is why the bench MK3.5 cannot
    /// reproduce it, and why this test exists instead of a hardware check.
    /// </para>
    /// <para>
    /// The authority is asserted as well as the leaf, because chain building checks its dates too:
    /// backdating only the leaf would fix nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void BothCertificatesPredateTheEpochSoAClocklessPrinterCanConnect()
    {
        // Arrange
        PrinterCertificateAuthority authority = NewAuthority();

        // Act
        using X509Certificate2 ca = authority.EnsureAuthority();
        using X509Certificate2 leaf = authority.IssueLeaf(["192.168.13.238"]);

        // Assert
        // 1969-12-31 23:59:59 UTC is what (time_t)-1 resolves to; both must already be valid then.
        DateTime clocklessPrinterBelievesItIs = new(1969, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        ca.NotBefore.ToUniversalTime().Should().BeBefore(clocklessPrinterBelievesItIs,
                                                         "a printer whose RTC was never set reads 1969, and the CA's dates are checked too");
        leaf.NotBefore.ToUniversalTime().Should().BeBefore(clocklessPrinterBelievesItIs,
                                                           "otherwise a MINI on a LAN without internet can never complete a handshake");

        // And the far end still bounds it - this concedes the low end only.
        leaf.NotAfter.ToUniversalTime().Should().BeAfter(DateTime.UtcNow);
    }

    /// <summary>
    /// A leaf with no usable name is refused rather than issued empty.
    /// </summary>
    [Fact]
    public void IssuingWithNoNamesIsRefused()
    {
        // Arrange
        PrinterCertificateAuthority authority = NewAuthority();

        // Assert
        Assert.Throws<ArgumentException>(() => authority.IssueLeaf(["   ", string.Empty]));
    }

    /// <summary>
    /// The leaf Kestrel serves is issued on the first run and never reissued on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The names change constantly on a real machine - a VPN comes up, <c>docker0</c> appears, wifi
    /// gives way to ethernet - and reissuing on each would drop every printer connection as Kestrel
    /// picked up the new certificate, make the certificate a function of what the machine happened to
    /// look like at boot, and silently expand what this server claims to be. So the second start gets
    /// the certificate the first one issued, whatever the addresses say now.
    /// </para>
    /// <para>
    /// Which is what leaves step 6 a real job: notice the drift, and offer the reissue.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheLeafIsIssuedOnceAndNotReissuedWhenTheNamesChange()
    {
        // Act
        using X509Certificate2 first = NewAuthority().EnsureLeaf(["192.168.13.238"]);
        using X509Certificate2 second = NewAuthority().EnsureLeaf(["192.168.13.99", "homespool.lan"]); // a "restart", elsewhere

        // Assert
        second.Thumbprint.Should().Be(first.Thumbprint,
                                      "an automatic reissue would drop every live printer connection, and nobody asked for one");
        DnsNames(second).Should().BeEquivalentTo(["192.168.13.238"]);
        second.HasPrivateKey.Should().BeTrue("Kestrel serves this, so the key has to survive the round trip to disk");
    }

    /// <summary>
    /// A leaf issued before the proxy needed PEM gets the PEM written on the next start, without
    /// being reissued.
    /// </summary>
    /// <remarks>
    /// <b>This is the upgrade path, and it failed on a real stack before this test existed.</b> Every
    /// deployment issued a certificate by an earlier version has <c>printer.pfx</c> and no PEM at all;
    /// <see cref="PrinterCertificateAuthority.EnsureLeaf"/> sees the PKCS#12, returns it, and used to
    /// write nothing further - so nginx found no certificate, declined to serve the printer listener,
    /// and every printer stopped connecting. The only diagnostic was a proxy log line reading as
    /// "PrinterTls must be off", which is exactly the wrong thing to conclude.
    /// <para>
    /// The thumbprint assertion is the other half: exporting the existing leaf rather than issuing a
    /// new one keeps whatever names the operator deliberately covered.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnUpgradedDeploymentGetsThePemWithoutReissuingTheLeaf()
    {
        // Arrange - a deployment from before the proxy terminated printer TLS: PKCS#12, no PEM.
        PrinterCertificateAuthority authority = NewAuthority();
        using X509Certificate2 original = authority.IssueLeaf(["192.168.13.238"]);

        File.Delete(authority.LeafCertificatePemPath);
        File.Delete(authority.LeafKeyPemPath);

        // Act - a restart on the new version.
        using X509Certificate2 served = NewAuthority().EnsureLeaf(["something-else-entirely.lan"]);

        // Assert
        File.Exists(authority.LeafCertificatePemPath).Should().BeTrue(
            "nginx reads PEM and cannot read the PKCS#12, so without this the proxy has nothing to present");
        File.Exists(authority.LeafKeyPemPath).Should().BeTrue();

        served.Thumbprint.Should().Be(original.Thumbprint,
                                      "the existing leaf is exported, not reissued - reissuing would silently drop the names the "
                                      + "operator had covered");

        using X509Certificate2 fromPem = X509Certificate2.CreateFromPem(
            File.ReadAllText(authority.LeafCertificatePemPath));

        fromPem.Thumbprint.Should().Be(original.Thumbprint, "and the PEM has to be that same leaf");
    }

    /// <summary>
    /// Every name offered on the first run is covered, so the operator picking the wrong one to write
    /// into a printer's ini costs a re-downloaded bundle rather than a re-provisioned printer.
    /// </summary>
    [Fact]
    public void TheFirstRunLeafCoversEveryNameItWasOffered()
    {
        // Act
        using X509Certificate2 leaf = NewAuthority().EnsureLeaf(["homespool.lan", "192.168.13.238", "10.0.0.4"]);

        // Assert
        DnsNames(leaf).Should().BeEquivalentTo(["homespool.lan", "192.168.13.238", "10.0.0.4"]);
        PrinterCertificateAuthority.NamesOf(leaf).Should().BeEquivalentTo(DnsNames(leaf),
                                                                          "drift detection reads the names back through NamesOf, so it must see what was written");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
