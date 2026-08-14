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
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hs-dpcert-{Guid.NewGuid():N}");

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
        using X509Certificate2 first = DataProtectionCertificate.Ensure(_root, 5475, TimeProvider.System);
        using X509Certificate2 second = DataProtectionCertificate.Ensure(_root, 5475, TimeProvider.System);

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
        using X509Certificate2 certificate = DataProtectionCertificate.Ensure(_root, 5475, TimeProvider.System);

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
    /// The private key survives the round trip through the file, so the ring can be decrypted on a
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
        using (X509Certificate2 minted = DataProtectionCertificate.Ensure(_root, 5475, TimeProvider.System))
        {
            minted.HasPrivateKey.Should().BeTrue();
        }

        // Act
        using X509Certificate2 loaded = DataProtectionCertificate.Ensure(_root, 5475, TimeProvider.System);

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
        using X509Certificate2 certificate = DataProtectionCertificate.Ensure(_root, 5475, TimeProvider.System);

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

    private static IDataProtectionProvider NewProvider(X509Certificate2 certificate, string ringDirectory)
    {
        ServiceCollection services = new();

        services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(ringDirectory))
                .ProtectKeysWithCertificate(certificate)
                .UnprotectKeysWithAnyCertificate(certificate);

        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }

    /// <summary>
    /// The file is not readable by other accounts on the host.
    /// </summary>
    /// <remarks>
    /// It sits under <c>data/</c>, which is a mounted volume and what an operator is told to back up,
    /// so the permissions are the only thing separating it from the database it protects.
    /// </remarks>
    [Fact]
    public void TheFileIsOwnerReadableOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Act
        using X509Certificate2 certificate = DataProtectionCertificate.Ensure(_root, 5475, TimeProvider.System);

        // Assert
        UnixFileMode mode = File.GetUnixFileMode(Path.Combine(_root, "dataprotection.pfx"));

        mode.Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
