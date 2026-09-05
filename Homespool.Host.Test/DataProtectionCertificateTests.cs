using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using AwesomeAssertions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Certificates;

namespace Homespool.Host.Test;

/// <summary>
/// The certificate that encrypts the Data Protection key ring at rest.
/// </summary>
/// <remarks>
/// The two properties worth pinning both fail quietly rather than loudly: an EC key is rejected only
/// when the first key is written, and a certificate minted twice leaves every existing key
/// undecryptable, which presents as every session ending at once rather than as an error here.
/// </remarks>
public sealed class DataProtectionCertificateTests : IDisposable
{
    private const string Passphrase = "unit test passphrase";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hs-dpcert-{Guid.NewGuid():N}");

    private string CertificatePath => Path.Combine(_root, "dataprotection.crt.pem");

    private string KeyPath => Path.Combine(_root, "dataprotection.key.pem");

    private string LegacyPath => Path.Combine(_root, "dataprotection.pfx");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// A second call returns the certificate the first one minted, rather than replacing it.
    /// </summary>
    /// <remarks>
    /// This is the property the whole class exists for. Re-minting on restart would not fail or log;
    /// it would leave every key already in the ring undecryptable, so every signed-in account and
    /// every outstanding password-reset link would break at the next deployment.
    /// </remarks>
    [Fact]
    public void TheCertificateIsMintedOnceAndThenReused()
    {
        // Act
        using X509Certificate2 first = DataProtectionCertificate.Ensure(_root, 5475, Passphrase, TimeProvider.System);
        using X509Certificate2 second = DataProtectionCertificate.Ensure(_root, 5475, Passphrase, TimeProvider.System);

        // Assert
        second.Thumbprint.Should().Be(first.Thumbprint);
    }

    /// <summary>
    /// The key is RSA, not the ECDSA the rest of this project's certificates use.
    /// </summary>
    /// <remarks>
    /// Data Protection encrypts the key ring through <c>EncryptedXml</c>, which is RSA key transport
    /// and rejects an EC public key. A "consistency" change to ECDSA would match the printer
    /// authority and break startup.
    /// </remarks>
    [Fact]
    public void TheKeyIsRsa()
    {
        // Act
        using X509Certificate2 certificate = DataProtectionCertificate.Ensure(_root, 5475, Passphrase, TimeProvider.System);

        // Assert
        using (RSA? rsa = certificate.GetRSAPublicKey())
        {
            rsa.Should().NotBeNull();
        }

        using (ECDsa? ecdsa = certificate.GetECDsaPublicKey())
        {
            ecdsa.Should().BeNull();
        }
    }

    /// <summary>
    /// The private key survives the round trip through the files, so the ring can be decrypted on a
    /// later run.
    /// </summary>
    /// <remarks>
    /// Encryption needs only the public half, so a certificate that loaded without its private key
    /// would protect keys perfectly and fail to read them back after a restart.
    /// </remarks>
    [Fact]
    public void TheLoadedCertificateHasItsPrivateKey()
    {
        // Arrange
        using (X509Certificate2 minted = DataProtectionCertificate.Ensure(_root, 5475, Passphrase, TimeProvider.System))
        {
            minted.HasPrivateKey.Should().BeTrue();
        }

        // Act
        using X509Certificate2 loaded = DataProtectionCertificate.Ensure(_root, 5475, Passphrase, TimeProvider.System);

        // Assert
        loaded.HasPrivateKey.Should().BeTrue();
    }

    /// <summary>
    /// A payload protected by one process is readable by the next, and the key ring on disk is
    /// encrypted rather than plaintext.
    /// </summary>
    /// <remarks>
    /// The end-to-end check, wiring Data Protection exactly as the host does. It is what would have
    /// caught the RSA constraint on its own — an EC certificate fails when the first key is written,
    /// not when it is minted — and the second provider is the part that matters: encrypting a ring
    /// nobody can read back is the failure this whole arrangement risks.
    /// </remarks>
    [Fact]
    public void AProtectedPayloadSurvivesARestartAndTheRingIsNotPlaintext()
    {
        // Arrange
        using X509Certificate2 certificate = DataProtectionCertificate.Ensure(_root, 5475, Passphrase, TimeProvider.System);

        string ringDirectory = Path.Combine(_root, "ring");

        Directory.CreateDirectory(ringDirectory);

        // Act
        string protectedPayload = NewProvider(certificate, ringDirectory)
                                  .CreateProtector("test").Protect("the-access-code");

        string unprotected = NewProvider(certificate, ringDirectory)
                             .CreateProtector("test").Unprotect(protectedPayload);

        // Assert
        unprotected.Should().Be("the-access-code");

        string ring = string.Concat(Directory.EnumerateFiles(ringDirectory, "*.xml")
                                             .Select(File.ReadAllText));

        ring.Should().NotBeEmpty();
        ring.Should().Contain("EncryptedData", "the key ring must not be written in the clear");
        ring.Should().NotContain("<value>", "that element carries the raw master key");
    }

    /// <summary>
    /// Both files are unreadable by other accounts on the host.
    /// </summary>
    /// <remarks>
    /// They sit under <c>data/</c>, which is a mounted volume and what an operator is told to back up,
    /// so the permissions are one of the two things separating the key from the database it protects.
    /// </remarks>
    [Fact]
    public void TheFilesAreOwnerReadableOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Act
        using X509Certificate2 certificate = DataProtectionCertificate.Ensure(_root, 5475, Passphrase, TimeProvider.System);

        // Assert
        File.GetUnixFileMode(KeyPath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(CertificatePath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>
    /// No passphrase is a refusal, before anything touches the disk - the same gate the printer
    /// authority keeps. Minting an unprotected key "for now" would be the state this arrangement
    /// exists to remove, and minting under an empty passphrase would be no protection at all.
    /// </summary>
    [Fact]
    public void AnEmptyPassphraseIsRefusedBeforeAnythingIsWritten()
    {
        // Act
        Action act = () => DataProtectionCertificate.Ensure(_root, 5475, string.Empty, TimeProvider.System);

        // Assert
        act.Should().Throw<DataProtectionCertificateUnreadableException>();
        File.Exists(KeyPath).Should().BeFalse("a misconfigured start must leave no trace");
        File.Exists(CertificatePath).Should().BeFalse();
    }

    /// <summary>
    /// The key on disk is useless without the passphrase, which is the whole of what this buys: a
    /// copied <c>data/</c> holds the database and these files, and can open neither key on the
    /// volume. And it is the authority's own layout - an encrypted PKCS#8 beside a certificate PEM -
    /// so the two read the same way.
    /// </summary>
    [Fact]
    public void TheKeyCannotBeOpenedWithoutThePassphrase()
    {
        // Arrange
        using X509Certificate2 certificate = DataProtectionCertificate.Ensure(_root, 5475, Passphrase, TimeProvider.System);

        // Act
        Action asPlaintext = () => X509Certificate2.CreateFromPemFile(CertificatePath, KeyPath).Dispose();
        Action wrongPassphrase = () => X509Certificate2.CreateFromEncryptedPemFile(CertificatePath, "not it", KeyPath).Dispose();

        // Assert
        File.ReadAllText(KeyPath).Should().StartWith("-----BEGIN ENCRYPTED PRIVATE KEY-----");
        asPlaintext.Should().Throw<CryptographicException>();
        wrongPassphrase.Should().Throw<CryptographicException>();
    }

    /// <summary>
    /// A key the configured passphrase does not open is a refusal to start, never a re-mint - the
    /// re-mint would not fail, it would silently orphan every key in the ring. The files are left
    /// exactly as they were, so the right passphrase still opens them afterwards.
    /// </summary>
    [Fact]
    public void AWrongPassphraseRefusesToStartAndReMintsNothing()
    {
        // Arrange
        string thumbprint;

        using (X509Certificate2 minted = DataProtectionCertificate.Ensure(_root, 5475, Passphrase, TimeProvider.System))
        {
            thumbprint = minted.Thumbprint;
        }

        // Act
        Action act = () => DataProtectionCertificate.Ensure(_root, 5475, "a different passphrase", TimeProvider.System);

        // Assert
        act.Should().Throw<DataProtectionCertificateUnreadableException>()
           .WithInnerException<CryptographicException>("the loader's own failure is what the operator will want to see");

        using X509Certificate2 stillThere = DataProtectionCertificate.Ensure(_root, 5475, Passphrase, TimeProvider.System);
        stillThere.Thumbprint.Should().Be(thumbprint, "the refusal must not have touched the files");
    }

    /// <summary>
    /// A certificate whose key beside it is gone is a refusal, not a fresh mint: the certificate on
    /// its own says which key the ring needs, and a new one would orphan every key in it.
    /// </summary>
    [Fact]
    public void ACertificateWithoutItsKeyRefusesToStartAndReMintsNothing()
    {
        // Arrange
        DataProtectionCertificate.Ensure(_root, 5475, Passphrase, TimeProvider.System).Dispose();
        File.Delete(KeyPath);

        // Act
        Action act = () => DataProtectionCertificate.Ensure(_root, 5475, Passphrase, TimeProvider.System);

        // Assert
        act.Should().Throw<DataProtectionCertificateUnreadableException>();
        File.Exists(KeyPath).Should().BeFalse("nothing may be minted in the gap");
    }

    /// <summary>
    /// A passwordless PKCS#12 from before the passphrase existed - what every deployment before
    /// this change has on its volume - is moved to the encrypted pair on the first start and then
    /// deleted, and is the same certificate afterwards, so nothing in the ring becomes unreadable.
    /// A migration that wrote the pair and died before deleting the PKCS#12 is finished on the
    /// next start.
    /// </summary>
    [Fact]
    public void APkcs12WrittenWithoutAPassphraseIsMovedToTheEncryptedPairAndDeleted()
    {
        // Arrange - what an existing deployment has: minted by an earlier version, exported with no
        // password. Deliberately not through the class, which can no longer produce one.
        Directory.CreateDirectory(_root);

        string thumbprint;
        byte[] legacyBytes;

        using (RSA key = RSA.Create(2048))
        {
            CertificateRequest request = new("CN=legacy", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            using X509Certificate2 legacy = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

            legacyBytes = legacy.Export(X509ContentType.Pkcs12);
            thumbprint = legacy.Thumbprint;
        }

        File.WriteAllBytes(LegacyPath, legacyBytes);

        // Act
        using X509Certificate2 migrated = DataProtectionCertificate.Ensure(_root, 5475, Passphrase, TimeProvider.System);

        // Assert
        migrated.Thumbprint.Should().Be(thumbprint, "the certificate must be the one the ring was encrypted with");
        migrated.HasPrivateKey.Should().BeTrue();

        File.Exists(LegacyPath).Should().BeFalse("the plaintext copy is deleted once the pair is proven readable");
        File.Exists(KeyPath + ".tmp").Should().BeFalse("the write is atomic and leaves nothing beside the key");
        File.ReadAllText(KeyPath).Should().StartWith("-----BEGIN ENCRYPTED PRIVATE KEY-----");

        using (X509Certificate2 again = DataProtectionCertificate.Ensure(_root, 5475, Passphrase, TimeProvider.System))
        {
            again.Thumbprint.Should().Be(thumbprint, "and the next start simply opens the pair");
        }

        // A migration that died between writing the pair and deleting the PKCS#12.
        File.WriteAllBytes(LegacyPath, legacyBytes);

        using X509Certificate2 finished = DataProtectionCertificate.Ensure(_root, 5475, Passphrase, TimeProvider.System);

        finished.Thumbprint.Should().Be(thumbprint, "the pair wins over the leftover");
        File.Exists(LegacyPath).Should().BeFalse("and the leftover is removed");
    }

    private static IDataProtectionProvider NewProvider(X509Certificate2 certificate, string ringDirectory)
    {
        ServiceCollection services = new();

        services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(ringDirectory))
                .ProtectKeysWithCertificate(certificate)
                .UnprotectKeysWithAnyCertificate(certificate);

        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }
}
