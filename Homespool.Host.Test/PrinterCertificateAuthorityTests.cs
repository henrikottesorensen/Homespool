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

    private PrinterCertificateAuthority NewAuthority(string passphrase = "unit test passphrase")
    {
        return new(Options.Create(new CertificateOptions { Directory = "certs", AuthorityPassphrase = passphrase }),
                   new HostEnvironmentAccessor(_root),
                   TimeProvider.System,
                   NullLogger<PrinterCertificateAuthority>.Instance,
                   new PrinterLeafChangeToken());
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

        // The proxy is what serves this, from its own directory - the application itself never
        // needs the leaf's key again, and holds no second copy of it.
        second.HasPrivateKey.Should().BeFalse("the key's only copy belongs to the proxy directory");
        File.Exists(NewAuthority().LeafKeyPemPath).Should().BeTrue("nginx has to find the key beside the certificate");
    }

    /// <summary>
    /// A leaf issued by an earlier version as PKCS#12 becomes the proxy's PEM pair on the next start,
    /// without being reissued.
    /// </summary>
    /// <remarks>
    /// <b>This is the upgrade path, and its ancestor failed on a real stack before this test
    /// existed.</b> A deployment issued a certificate before nginx terminated printer TLS has
    /// <c>printer.pfx</c> and no PEM at all; returning the PKCS#12 without writing the PEMs leaves
    /// nginx with no certificate, a proxy log line reading as "PrinterTls must be off", and every
    /// printer unable to connect.
    /// <para>
    /// The thumbprint assertion is the other half: migrating the existing leaf rather than issuing a
    /// new one keeps whatever names the operator deliberately covered. And the PKCS#12 must be gone
    /// afterwards - it holds the private key in the clear with nothing reading it.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnUpgradedDeploymentGetsThePemWithoutReissuingTheLeaf()
    {
        // Arrange - a deployment from an earlier version: PKCS#12 beside the authority, no PEM.
        PrinterCertificateAuthority authority = NewAuthority();
        using X509Certificate2 original = authority.IssueLeaf(["192.168.13.238"]);

        string legacyPath = Path.Combine(_root, "certs", "printer.pfx");

        using (X509Certificate2 legacy = X509Certificate2.CreateFromPemFile(
                   authority.LeafCertificatePemPath, authority.LeafKeyPemPath))
        {
            File.WriteAllBytes(legacyPath, legacy.Export(X509ContentType.Pkcs12));
        }

        File.Delete(authority.LeafCertificatePemPath);
        File.Delete(authority.LeafKeyPemPath);

        // Act - a restart on the new version.
        using X509Certificate2 served = NewAuthority().EnsureLeaf(["something-else-entirely.lan"]);

        // Assert
        File.Exists(authority.LeafCertificatePemPath).Should().BeTrue(
            "nginx reads PEM and cannot read the PKCS#12, so without this the proxy has nothing to present");
        File.Exists(authority.LeafKeyPemPath).Should().BeTrue();

        served.Thumbprint.Should().Be(original.Thumbprint,
                                      "the existing leaf is migrated, not reissued - reissuing would silently drop the names the "
                                      + "operator had covered");

        using X509Certificate2 fromPem = X509Certificate2.CreateFromPem(
            File.ReadAllText(authority.LeafCertificatePemPath));

        fromPem.Thumbprint.Should().Be(original.Thumbprint, "and the PEM has to be that same leaf");

        File.Exists(legacyPath).Should().BeFalse(
            "the PKCS#12 holds the private key in the clear, and after migration nothing reads it");
    }

    /// <summary>
    /// Minting under a configured passphrase writes the authority's key encrypted, and the same
    /// passphrase opens it on the next start.
    /// </summary>
    /// <remarks>
    /// The header assertion is the honest check: a PKCS#8 PEM announces its own encryption state, and
    /// "ENCRYPTED PRIVATE KEY" in the file is what a copied <c>data/</c> backup would carry instead of
    /// the key.
    /// </remarks>
    [Fact]
    public void MintingWithAPassphraseEncryptsTheAuthorityKey()
    {
        // Act
        PrinterCertificateAuthority authority = NewAuthority("correct horse battery staple");
        using X509Certificate2 minted = authority.EnsureAuthority();
        using X509Certificate2 reloaded = NewAuthority("correct horse battery staple").EnsureAuthority();

        // Assert
        File.ReadAllText(authority.AuthorityKeyPemPath).Should().Contain("ENCRYPTED PRIVATE KEY",
                                                                         "an unencrypted key here is exactly what the passphrase exists to prevent");
        reloaded.Thumbprint.Should().Be(minted.Thumbprint);
        reloaded.GetECDsaPrivateKey().Should().NotBeNull("the authority must still be able to sign leaves");
    }

    /// <summary>
    /// An empty passphrase is a refusal before anything touches the disk — there is no
    /// plaintext-at-rest mode to configure into by accident.
    /// </summary>
    /// <remarks>
    /// Both directions of the mistake: a fresh deployment must not mint an unencrypted key it would
    /// then live with forever, and an existing one must not have its startup interpreted as "no
    /// passphrase, carry on" when the variable was lost. The no-files assertion is the fresh half —
    /// a refused start leaves nothing behind to migrate or trip over.
    /// </remarks>
    [Fact]
    public void AnEmptyPassphraseRefusesAndMintsNothing()
    {
        // Arrange
        PrinterCertificateAuthority unconfigured = NewAuthority(string.Empty);

        // Act
        Assert.Throws<CertificateAuthorityUnreadableException>(() => unconfigured.EnsureAuthority());

        // Assert
        File.Exists(unconfigured.AuthorityKeyPemPath).Should().BeFalse("a refused start must leave no trace");
        File.Exists(unconfigured.AuthorityCertificatePemPath).Should().BeFalse();

        // And the same refusal once an authority exists and the variable goes missing.
        NewAuthority().EnsureAuthority().Dispose();

        Assert.Throws<CertificateAuthorityUnreadableException>(() => NewAuthority(string.Empty).EnsureAuthority());
    }

    /// <summary>
    /// A key decrypted by hand is re-encrypted under the configured passphrase on the next start,
    /// which is the passphrase-rotation path.
    /// </summary>
    /// <remarks>
    /// Rotation has no first-class command on purpose — it is decrypt with <c>openssl pkcs8</c>,
    /// change the value, restart — and this is the half the application owns: a plaintext key on
    /// disk plus a configured passphrase means "encrypt under this one now". The empty-passphrase
    /// refusal is what keeps the rotation from ever quietly ending at "no encryption".
    /// </remarks>
    [Fact]
    public void AHandDecryptedKeyIsReEncryptedUnderTheNewPassphrase()
    {
        // Arrange - an authority under the old passphrase, its key then decrypted by hand.
        PrinterCertificateAuthority oldPassphrase = NewAuthority("the old passphrase");
        using X509Certificate2 minted = oldPassphrase.EnsureAuthority();

        using (X509Certificate2 opened = X509Certificate2.CreateFromEncryptedPemFile(
                   oldPassphrase.AuthorityCertificatePemPath, "the old passphrase", oldPassphrase.AuthorityKeyPemPath))
        using (ECDsa key = opened.GetECDsaPrivateKey()!)
        {
            File.WriteAllText(oldPassphrase.AuthorityKeyPemPath, key.ExportPkcs8PrivateKeyPem());
        }

        // Act - the operator sets the new value and restarts.
        using X509Certificate2 rotated = NewAuthority("the new passphrase").EnsureAuthority();

        // Assert
        rotated.Thumbprint.Should().Be(minted.Thumbprint, "rotating the passphrase must not touch the authority itself");
        File.ReadAllText(oldPassphrase.AuthorityKeyPemPath).Should().Contain("ENCRYPTED PRIVATE KEY",
                                                                             "the plaintext interlude must end at the first start");

        Assert.Throws<CertificateAuthorityUnreadableException>(() => NewAuthority("the old passphrase").EnsureAuthority());
    }

    /// <summary>
    /// A wrong passphrase is a refusal that leaves everything on disk untouched.
    /// </summary>
    /// <remarks>
    /// The recovery this protects: the operator restores the right value in <c>.env</c> and starts
    /// again. Anything that "handled" the failure by rewriting files - a re-mint most of all - would
    /// turn a typo into a fleet-wide USB visit.
    /// </remarks>
    [Fact]
    public void AWrongPassphraseRefusesAndLeavesTheAuthorityIntact()
    {
        // Arrange
        using X509Certificate2 minted = NewAuthority("the real passphrase").EnsureAuthority();

        // Act
        Assert.Throws<CertificateAuthorityUnreadableException>(() => NewAuthority("a typo").EnsureAuthority());

        // Assert - the right passphrase still opens the same authority.
        using X509Certificate2 recovered = NewAuthority("the real passphrase").EnsureAuthority();

        recovered.Thumbprint.Should().Be(minted.Thumbprint,
                                         "a failed start must leave the deployment exactly as it found it");
    }

    /// <summary>
    /// A key whose certificate and passphrase are both gone refuses; a key that merely lost its
    /// certificate PEM heals from <c>connect.der</c>.
    /// </summary>
    /// <remarks>
    /// The certificate is public and <c>connect.der</c> is a byte-for-byte copy, so losing the PEM is
    /// repairable. Losing the <i>key</i> is not - it exists nowhere else - which is why that arm is a
    /// refusal naming a backup rather than a fresh mint.
    /// </remarks>
    [Fact]
    public void ALostKeyRefusesButALostCertificateHealsFromTheDer()
    {
        // Arrange
        PrinterCertificateAuthority authority = NewAuthority();
        using X509Certificate2 minted = authority.EnsureAuthority();

        // Act + Assert - certificate PEM gone, DER present: repaired.
        File.Delete(authority.AuthorityCertificatePemPath);

        using (X509Certificate2 healed = NewAuthority().EnsureAuthority())
        {
            healed.Thumbprint.Should().Be(minted.Thumbprint, "connect.der carries the same certificate");
        }

        File.Exists(authority.AuthorityCertificatePemPath).Should().BeTrue();

        // Key gone: a refusal, never a replacement authority.
        File.Delete(authority.AuthorityKeyPemPath);

        Assert.Throws<CertificateAuthorityUnreadableException>(() => NewAuthority().EnsureAuthority());
    }

    /// <summary>
    /// An authority stored by an earlier version as passwordless PKCS#12 migrates to the PEM pair -
    /// encrypted, when a passphrase is configured - and the PKCS#12 is deleted.
    /// </summary>
    /// <remarks>
    /// The fixture builds its own <c>ca.pfx</c> the way earlier versions wrote it, because the class
    /// no longer can. The thumbprint assertion is the fleet-safety half: migration must carry the
    /// same authority across, or every provisioned printer stops validating.
    /// </remarks>
    [Fact]
    public void ALegacyPkcs12AuthorityMigratesToAnEncryptedPemPair()
    {
        // Arrange - ca.pfx and connect.der as an earlier version left them.
        string directory = Path.Combine(_root, "certs");

        Directory.CreateDirectory(directory);

        string thumbprint;

        using (ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            CertificateRequest request = new("CN=Homespool printer CA", key, HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
                                                  certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0,
                                                  critical: true));

            using X509Certificate2 legacy = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

            File.WriteAllBytes(Path.Combine(directory, "ca.pfx"), legacy.Export(X509ContentType.Pkcs12));
            File.WriteAllBytes(Path.Combine(directory, "connect.der"), legacy.Export(X509ContentType.Cert));
            thumbprint = legacy.Thumbprint;
        }

        // Act - the first start on the new version, with a passphrase configured.
        PrinterCertificateAuthority authority = NewAuthority("hunter2");
        using X509Certificate2 migrated = authority.EnsureAuthority();

        // Assert
        migrated.Thumbprint.Should().Be(thumbprint,
                                        "migration must carry the same authority across, or every provisioned printer is stranded");
        File.Exists(Path.Combine(directory, "ca.pfx")).Should().BeFalse(
            "the PKCS#12 holds the key in the clear, which is what the migration exists to end");
        File.ReadAllText(authority.AuthorityKeyPemPath).Should().Contain("ENCRYPTED PRIVATE KEY");

        using X509Certificate2 reloaded = NewAuthority("hunter2").EnsureAuthority();

        reloaded.Thumbprint.Should().Be(thumbprint, "and the migrated pair has to survive a restart");
    }

    /// <summary>
    /// The authority's certificate is readable without the passphrase, and reading it never mints.
    /// </summary>
    /// <remarks>
    /// The health check runs on every probe and only wants the expiry date, which is public. Wiring
    /// it through the key would make every probe pay a key derivation and - far worse - would give a
    /// probe a path to minting; this is the accessor that makes both impossible.
    /// </remarks>
    [Fact]
    public void TheAuthorityCertificateIsReadableWithoutThePassphrase()
    {
        // Arrange - nothing minted yet: reading must not become the reason an authority exists.
        NewAuthority("sealed").LoadAuthorityCertificate().Should().BeNull("reading never mints");

        using X509Certificate2 minted = NewAuthority("sealed").EnsureAuthority();

        // Act - a caller holding no passphrase at all.
        using X509Certificate2? certificate = NewAuthority(string.Empty).LoadAuthorityCertificate();

        // Assert
        certificate.Should().NotBeNull();
        certificate!.Thumbprint.Should().Be(minted.Thumbprint);
        certificate.HasPrivateKey.Should().BeFalse("the public half is all this accessor may hand out");
    }

    /// <summary>
    /// A deleted <c>connect.der</c> is rewritten from the authority on the next start.
    /// </summary>
    /// <remarks>
    /// The DER is a public copy of the certificate, so it is the one authority file whose loss is
    /// silently repairable - and it must be, because every provisioning bundle reads it.
    /// </remarks>
    [Fact]
    public void ADeletedDerIsRewrittenOnTheNextStart()
    {
        // Arrange
        PrinterCertificateAuthority authority = NewAuthority();
        using X509Certificate2 minted = authority.EnsureAuthority();

        File.Delete(authority.AuthorityDerPath);

        // Act
        NewAuthority().EnsureAuthority().Dispose();

        // Assert
        using X509Certificate2 rewritten = X509CertificateLoader.LoadCertificate(
            File.ReadAllBytes(authority.AuthorityDerPath));

        rewritten.Thumbprint.Should().Be(minted.Thumbprint, "a bundle built tomorrow must carry the same anchor");
    }

    /// <summary>
    /// Half a leaf pair is repaired by reissuing, because half a pair serves nobody.
    /// </summary>
    /// <remarks>
    /// The contrast with the authority's refusal arms is the point: the leaf is the replaceable kind
    /// of secret - printers trust the authority, not the leaf - so repair is free, where "repairing"
    /// the authority would strand the fleet.
    /// </remarks>
    [Fact]
    public void HalfALeafPairIsRepairedByReissuing()
    {
        // Arrange
        PrinterCertificateAuthority authority = NewAuthority();
        using X509Certificate2 first = authority.EnsureLeaf(["homespool.lan"]);

        File.Delete(authority.LeafKeyPemPath);

        // Act
        using X509Certificate2 second = NewAuthority().EnsureLeaf(["homespool.lan"]);

        // Assert
        second.Thumbprint.Should().NotBe(first.Thumbprint, "a leaf whose key is gone cannot be served, only replaced");
        File.Exists(authority.LeafCertificatePemPath).Should().BeTrue();
        File.Exists(authority.LeafKeyPemPath).Should().BeTrue();
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
