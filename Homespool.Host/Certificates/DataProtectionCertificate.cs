using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

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
/// <b>The certificate is on disk while the ring it protects is in the database, and that separation
/// is the entire point.</b> Key and lock in one SQLite file would leave a stolen copy of that file
/// just as useful as before. It is the same reasoning
/// <see cref="CertificateOptions.Directory"/> already records for the printer authority: a separate
/// file can be excluded from a backup, permissioned, and noticed; a row cannot. The honest limit is
/// that an operator who copies all of <c>data/</c> takes both halves anyway — what this buys is that
/// a database-only disclosure, a support bundle or an injected read, stops being enough.
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
    private const string FileName = "dataprotection.pfx";

    /// <summary>
    /// Returns the key-protection certificate, creating it on first call and loading it every time
    /// after.
    /// </summary>
    /// <remarks>
    /// <b>Idempotence matters here for the same reason it does when minting the printer
    /// authority.</b> A second certificate would not fail or look wrong; it would leave every key
    /// already in the ring undecryptable, which presents as every session and every outstanding
    /// password-reset link breaking at once. So an existing file always wins, and nothing here
    /// rotates anything.
    /// </remarks>
    /// <param name="directory">Directory to hold the certificate. Created if absent.</param>
    /// <param name="validityDays">Lifetime to mint with, when there is no certificate yet.</param>
    /// <param name="time">Clock, so tests need not depend on the wall clock.</param>
    public static X509Certificate2 Ensure(string directory, int validityDays, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(time);

        System.IO.Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, FileName);

        if (File.Exists(path))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.Exportable);
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

        WriteFile(path, certificate.Export(X509ContentType.Pkcs12));

        return X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.Exportable);
    }

    private static void WriteFile(string path, byte[] contents)
    {
        File.WriteAllBytes(path, contents);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
