using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Homespool.Host.Certificates;

/// <summary>
/// Mints and holds the certificate that encrypts the Data Protection key ring at rest.
/// </summary>
/// <remarks>
/// <para>
/// Without this, the key ring is written to <c>DataProtectionKeys</c> as plaintext XML — the startup
/// warning "No XML encryptor configured" says exactly that — and those keys protect authentication
/// cookies, antiforgery tokens, and the email-confirmation and password-reset tokens Identity issues.
/// Anyone holding the database can mint a cookie for any account, so under an
/// assume-internet-facing threat model the plaintext ring is the finding, not a tidiness matter.
/// </para>
/// <para>
/// <b>The certificate is on disk while the ring it protects is in the database, and the key file is
/// encrypted under the same passphrase as the printer authority's.</b> The two halves of that answer
/// two different leaks. Key and lock in one SQLite file would leave a stolen copy of that file as
/// useful as before, so the certificate is a separate file: one that can be excluded from a backup,
/// permissioned, and noticed. But the file sits under <c>data/</c> beside the database, which is
/// exactly what a wandering backup or a <c>docker cp</c> carries — and a passwordless PKCS#12 there
/// handed such a copy every account's cookie while the authority key beside it, encrypted under
/// <see cref="CertificateOptions.AuthorityPassphrase"/>, gave up nothing. Under the same passphrase
/// both keys are useless without <c>.env</c>, which lives outside the volume and which operators are
/// already told to back up separately. Corrected 2026-09-03; until then this file's remarks conceded
/// the copied-volume case rather than defending it.
/// </para>
/// <para>
/// <b>Reusing the authority's passphrase rather than adding a second.</b> The two keys share every
/// scenario in which a passphrase helps or does not — a running container has it in its environment,
/// a host compromise reaches <c>.env</c>, a volume-only copy lacks it — so a second secret would add
/// one more thing to back up and lose without changing what any attacker gets. What differs is the
/// cost of losing each: this one costs every session and pending reset link until people sign in
/// again, the authority a USB visit to every printer. Both are worth the one passphrase. The
/// corollary is that this key's work factor cannot be lower than the authority's: with one
/// passphrase behind two files, the cheaper file is the one an attacker guesses against.
/// </para>
/// <para>
/// <b>A PEM pair, in the authority's layout, and not a PKCS#12.</b> A PKCS#12 under a passphrase
/// runs the key derivation twice on every open — once for its MAC, once for the key — and the MAC
/// cannot be made cheap without becoming the guessing oracle above. An encrypted PKCS#8 key beside a
/// certificate PEM has no MAC, opens with one derivation, and is what <c>ca.key.pem</c> already is,
/// so the two keys on this volume read the same way. The PKCS#12 an earlier version wrote is
/// migrated to the pair on first start and then deleted, exactly as <c>ca.pfx</c> was.
/// </para>
/// <para>
/// <b>RSA, where everything else here is ECDSA P-256, and it cannot be otherwise.</b> Data
/// Protection encrypts the ring through <c>EncryptedXml</c>, which is RSA key transport and rejects
/// an EC public key outright — so this cannot reuse
/// <see cref="PrinterCertificateAuthority"/>'s minting, and a "consistency" fix to ECDSA breaks
/// startup rather than anything subtle. The two certificates are unrelated in every other way too:
/// that one is a trust anchor printers verify against, this one never leaves the host.
/// </para>
/// <para>
/// <b>Self-signed, and validity is deliberately long.</b> Nothing verifies this certificate — it is
/// a key container, not an identity — so a chain, a name and an expiry buy nothing, while an expiry
/// that passes unnoticed would be an outage with a confusing message.
/// </para>
/// </remarks>
public static class DataProtectionCertificate
{
    private const string LegacyFileName = "dataprotection.pfx";
    private const string CertificatePemFileName = "dataprotection.crt.pem";
    private const string KeyPemFileName = "dataprotection.key.pem";

    /// <summary>
    /// How the key is encrypted: AES-256 under PBKDF2-SHA256 at the count
    /// <see cref="PrinterCertificateAuthority"/> uses for the authority key - the same passphrase
    /// guards both, so this cannot be the weaker of the two - and the file is opened once per start,
    /// so the cost lands nowhere hot.
    /// </summary>
    private static readonly PbeParameters KeyEncryption = new(
        PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);

    /// <summary>
    /// Returns the key-protection certificate, creating it on first call and loading it every time
    /// after.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Idempotence matters here for the same reason it does when minting the printer
    /// authority.</b> A second certificate would not fail or look wrong; it would leave every key
    /// already in the ring undecryptable, which presents as every session and every outstanding
    /// password-reset link breaking at once. So an existing key always wins, and nothing here rotates
    /// anything — a key that exists and cannot be opened is a refusal to start, never a re-mint, and
    /// so is a certificate whose key beside it is gone.
    /// </para>
    /// <para>
    /// <b>A PKCS#12 written before the passphrase existed is moved to the pair, once.</b> It loads
    /// with no password, its certificate and key are written out in the new layout and proved to
    /// open, and only then is it deleted. The certificate is unchanged, so nothing in the ring
    /// becomes unreadable; a migration that died between writing the pair and deleting the PKCS#12
    /// is finished on the next start.
    /// </para>
    /// </remarks>
    /// <param name="directory">Directory to hold the certificate. Created if absent.</param>
    /// <param name="validityDays">Lifetime to mint with, when there is no certificate yet.</param>
    /// <param name="passphrase">What the key file is encrypted under. Refused if empty, before
    /// anything touches the disk.</param>
    /// <param name="time">Clock, so tests need not depend on the wall clock.</param>
    public static X509Certificate2 Ensure(string directory, int validityDays, string passphrase, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(passphrase);
        ArgumentNullException.ThrowIfNull(time);

        if (passphrase.Length == 0)
        {
            throw new DataProtectionCertificateUnreadableException(
                "No Certificates:AuthorityPassphrase is configured (CA_PASSPHRASE in .env on the shipped stack; " +
                "setup-env.sh generates one), and the Data Protection key is never handled without one. " +
                "If a key encrypted under a previous passphrase exists, only that exact value can open it - " +
                "nothing here will mint a replacement, because that would invalidate every session and pending token.");
        }

        System.IO.Directory.CreateDirectory(directory);

        string certificatePath = Path.Combine(directory, CertificatePemFileName);
        string keyPath = Path.Combine(directory, KeyPemFileName);
        string legacyPath = Path.Combine(directory, LegacyFileName);

        if (File.Exists(keyPath))
        {
            if (!File.Exists(certificatePath))
            {
                throw new DataProtectionCertificateUnreadableException(
                    $"The Data Protection key is in {keyPath} but its certificate ({certificatePath}) is gone. Restore " +
                    "it from a backup; nothing here will mint a replacement, because that would invalidate every " +
                    "session and pending token.");
            }

            X509Certificate2 loaded = LoadPair(certificatePath, keyPath, passphrase);

            if (File.Exists(legacyPath))
            {
                // A migration that wrote its pair and then died before this line. The pair is verified
                // readable, so the plaintext PKCS#12 is the one copy too many.
                File.Delete(legacyPath);
            }

            return loaded;
        }

        if (File.Exists(certificatePath))
        {
            throw new DataProtectionCertificateUnreadableException(
                $"{certificatePath} exists but the private key beside it ({keyPath}) is gone. Restore the key from a " +
                "backup; it cannot be recreated, and a fresh one would invalidate every session and pending token.");
        }

        if (File.Exists(legacyPath))
        {
            return MigrateFromPkcs12(legacyPath, certificatePath, keyPath, passphrase);
        }

        using RSA key = RSA.Create(3072);

        CertificateRequest request = new("CN=Homespool data protection", key,
                                         HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509KeyUsageExtension(
                                              X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment,
                                              critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        DateTimeOffset now = time.GetUtcNow();
        using X509Certificate2 certificate = request.CreateSelfSigned(
            now.AddDays(-1), now.AddDays(validityDays));

        return WritePair(certificatePath, keyPath, certificate, key, passphrase);
    }

    private static X509Certificate2 LoadPair(string certificatePath, string keyPath, string passphrase)
    {
        try
        {
            return X509Certificate2.CreateFromEncryptedPemFile(certificatePath, passphrase, keyPath);
        }
        catch (CryptographicException exception)
        {
            throw new DataProtectionCertificateUnreadableException(
                $"The Data Protection key ({keyPath}) exists but cannot be opened with the configured " +
                "Certificates:AuthorityPassphrase. Only the passphrase it was written under can open it; nothing here " +
                "will mint a replacement, because that would invalidate every session and pending token.",
                exception);
        }
    }

    /// <summary>
    /// Moves a passwordless PKCS#12 written by an earlier version to the encrypted pair, then deletes
    /// it. Nothing is re-minted: the certificate the ring was encrypted with is the one written out.
    /// </summary>
    private static X509Certificate2 MigrateFromPkcs12(string legacyPath, string certificatePath, string keyPath, string passphrase)
    {
        X509Certificate2 legacy;

        try
        {
            legacy = X509CertificateLoader.LoadPkcs12FromFile(legacyPath, null, X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException exception)
        {
            throw new DataProtectionCertificateUnreadableException(
                $"The Data Protection certificate ({legacyPath}) cannot be read. Restore it from a backup; nothing " +
                "here will mint a replacement, because that would invalidate every session and pending token.",
                exception);
        }

        X509Certificate2 migrated;

        using (legacy)
        {
            using RSA key = legacy.GetRSAPrivateKey() ??
                throw new DataProtectionCertificateUnreadableException(
                    $"The Data Protection certificate ({legacyPath}) carries no RSA private key, so it cannot decrypt " +
                    "the ring and cannot be migrated. Restore it from a backup; nothing here will mint a replacement, " +
                    "because that would invalidate every session and pending token.");

            migrated = WritePair(certificatePath, keyPath, legacy, key, passphrase);
        }

        File.Delete(legacyPath);

        return migrated;
    }

    /// <summary>
    /// Writes the certificate, then the key encrypted under <paramref name="passphrase"/> to a
    /// temporary file, proves the pair opens, and only then moves the key into place - so a write
    /// that fails part-way leaves whatever was there before, never a half-written key.
    /// </summary>
    private static X509Certificate2 WritePair(string certificatePath,
                                              string keyPath,
                                              X509Certificate2 certificate,
                                              RSA key,
                                              string passphrase)
    {
        WriteFile(certificatePath, Encoding.ASCII.GetBytes(certificate.ExportCertificatePem()));

        string temporary = keyPath + ".tmp";

        WriteFile(temporary, Encoding.ASCII.GetBytes(key.ExportEncryptedPkcs8PrivateKeyPem(passphrase, KeyEncryption)));

        X509Certificate2? verified = null;

        try
        {
            verified = X509Certificate2.CreateFromEncryptedPemFile(certificatePath, passphrase, temporary);

            if (!verified.HasPrivateKey)
            {
                throw new DataProtectionCertificateUnreadableException(
                    $"The freshly written Data Protection key ({temporary}) came back without its private key, so nothing was replaced.");
            }

            File.Move(temporary, keyPath, overwrite: true);

            X509Certificate2 proven = verified;

            verified = null;

            return proven;
        }
        finally
        {
            verified?.Dispose();

            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// Owner read-write and nothing else, the certificate included: it is public material, but
    /// nothing else on this host ever reads it, so there is no reader to widen it for.
    /// </summary>
    private static void WriteFile(string path, byte[] contents)
    {
        File.WriteAllBytes(path, contents);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
