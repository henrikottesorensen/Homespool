using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Certificates;

/// <summary>
/// Mints and holds the certificate authority a provisioned printer trusts, and the leaf Kestrel
/// serves on the printer listener.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is shaped by what Buddy firmware will accept, none of which is negotiable:
/// <b>ECDSA P-256</b>, because the firmware compiles exactly one ciphersuite
/// (<c>ECDHE-ECDSA-AES128-GCM-SHA256</c>) and an RSA certificate shares none with it; <b>DER</b>,
/// because PEM is unsupported and a renamed file fails as <c>Error::Tls</c> with no explanation; and
/// names carried as <b>dNSName</b> SAN entries even when they are IP addresses.
/// </para>
/// <para>
/// <b>The IP-as-dNSName encoding is the load-bearing oddity</b>, and it is deliberate rather than
/// sloppy. mbedTLS matches SAN entries by plain string comparison and never parses them as addresses,
/// so <c>192.168.1.50</c> in a dNSName entry matches a <c>hostname</c> of <c>192.168.1.50</c>. The
/// RFC-correct form — an iPAddress entry — reaches firmware's "unrecognised type" arm and is
/// <i>never</i> matched. Confirmed on an MK3.5 on 2026-07-29. Do not "fix" this to use
/// <c>AddIpAddress</c>; that is the form that fails.
/// </para>
/// <para>
/// <b>Everything is stored as PEM, with exactly one copy of each private key.</b> The authority is a
/// certificate and a key file beside it, so "which file is the secret" has a one-word answer, and
/// the key is always encrypted with a passphrase held outside the data volume
/// (<see cref="CertificateOptions.AuthorityPassphrase"/>). The leaf's only copy lives in the proxy
/// directory nginx reads — this process never needs the leaf's private key after issuing it, and a
/// second copy would be one more thing to keep in step. The PKCS#12 files earlier versions wrote
/// (<c>ca.pfx</c>, <c>printer.pfx</c>) are migrated to this layout on first sight and then deleted.
/// </para>
/// </remarks>
public class PrinterCertificateAuthority
{
    private const string LegacyAuthorityFileName = "ca.pfx";
    private const string LegacyLeafFileName = "printer.pfx";
    private const string AuthorityCertificatePemFileName = "ca.crt.pem";
    private const string AuthorityKeyPemFileName = "ca.key.pem";
    private const string AuthorityDerFileName = "connect.der";

    /// <summary>
    /// The leaf and its key in PEM, for a TLS terminator that cannot read PKCS#12 - nginx, which
    /// terminates the printer connection as of 2026-07-31.
    /// </summary>
    /// <remarks>
    /// <b>The certificate file holds the leaf alone, with no chain appended, and that is
    /// load-bearing.</b> Firmware's <c>x509_crt_check_ee_locally_trusted</c> requires exactly one
    /// certificate to be presented; a terminator that sends leaf + authority fails verification in a
    /// way that reads as a protocol bug rather than a certificate one.
    /// </remarks>
    private const string LeafCertificatePemFileName = "printer-leaf.pem";

    private const string LeafKeyPemFileName = "printer-leaf.key.pem";
    private const string SubjectAlternativeNameOid = "2.5.29.17";

    /// <summary>
    /// What a PKCS#8 PEM announces itself as when it is passphrase-encrypted, and the whole of how
    /// this class tells the two states apart — no trial decryption, no probing.
    /// </summary>
    private const string EncryptedKeyPemLabel = "ENCRYPTED PRIVATE KEY";

    /// <summary>
    /// How the authority's key is encrypted when a passphrase is configured.
    /// </summary>
    /// <remarks>
    /// AES-256 under PBKDF2 at OWASP's recommended count. The count defends a passphrase somebody
    /// typed; the generated one is 24 random bytes and needs no defending. It is affordable because
    /// nothing hot pays it: the key is decrypted once at startup and again only when a leaf is
    /// minted, while every routine reader — the health check, the provisioning bundle — takes the
    /// certificate, which is public and needs no passphrase.
    /// </remarks>
    private static readonly PbeParameters KeyEncryption = new(
        PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);

    /// <summary>
    /// What both certificates claim as <c>notBefore</c>: 1960, deliberately before the epoch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what lets a printer with no clock connect at all.</b> Buddy firmware's
    /// <c>time()</c> returns <b>-1</b> when the RTC has never been set (<c>sys_time.cpp</c>: "RTC was
    /// not initialize"), and <c>mbedtls_time</c> is a plain <c>#define</c> to it.
    /// <c>x509_get_current_time</c> does not treat -1 as an error — <c>gmtime_r(-1)</c> succeeds — so
    /// mbedTLS believes the date is <b>1969-12-31</b>. Against that, any ordinary certificate is
    /// <i>not yet valid</i>, and with <c>MBEDTLS_SSL_VERIFY_REQUIRED</c> and no verify callback the
    /// handshake simply fails.
    /// </para>
    /// <para>
    /// <b>Which printers this affects is a board question, and it is not the edge case it looks
    /// like.</b> The MINI's Buddy board ties the STM32's VBAT straight to +3.3V with no cell fitted
    /// (<c>prusa3d/Buddy-board-MINI-PCB</c>, <c>rev.1.0.0/cpu.sch</c>), so its RTC is lost at <i>every
    /// power-off</i> — on an isolated LAN a MINI could never establish TLS, and on a connected one it
    /// fails from power-on until SNTP lands. xBuddy boards (MK3.5, MK4, XL, Core One) carry a CR1220
    /// and keep time once set, which is why the bench MK3.5 cannot reproduce any of this.
    /// </para>
    /// <para>
    /// There is no other route to a date: the only writer of the RTC anywhere in firmware is the SNTP
    /// callback (<c>wui_api.cpp</c>), the server is hardcoded to <c>prusa3d.pool.ntp.org</c>, and the
    /// menu offers a timezone offset but no way to enter a time.
    /// </para>
    /// <para>
    /// So the low end of validity is conceded for printers that have no clock — they never had one,
    /// and the alternative is TLS that cannot work. <c>notAfter</c> still bounds the far end. 1960 is
    /// chosen to sit inside X.509 <c>UTCTime</c>'s 1950-2049 range. <b>Do not "correct" this to the
    /// issue date.</b>
    /// </para>
    /// </remarks>
    private static readonly DateTimeOffset NotBefore = new(1960, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _directory;
    private readonly string _proxyDirectory;
    private readonly CertificateOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PrinterCertificateAuthority> _logger;
    private readonly PrinterLeafChangeToken _leafChanged;

    public PrinterCertificateAuthority(IOptions<CertificateOptions> options,
                                       IHostEnvironmentAccessor environment,
                                       TimeProvider time,
                                       ILogger<PrinterCertificateAuthority> logger,
                                       PrinterLeafChangeToken leafChanged)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        _options = options.Value;
        _time = time;
        _logger = logger;
        _leafChanged = leafChanged;
        _directory = Path.IsPathRooted(_options.Directory) ?
            _options.Directory :
            Path.Combine(environment.ContentRootPath, _options.Directory);

        _proxyDirectory = Path.IsPathRooted(_options.ProxyDirectory) ?
            _options.ProxyDirectory :
            Path.Combine(environment.ContentRootPath, _options.ProxyDirectory);
    }

    /// <summary>Path of the DER-encoded authority, which is what goes on the USB stick.</summary>
    public string AuthorityDerPath => Path.Combine(_directory, AuthorityDerFileName);

    /// <summary>Path of the authority's certificate in PEM. Public, like every certificate.</summary>
    public string AuthorityCertificatePemPath => Path.Combine(_directory, AuthorityCertificatePemFileName);

    /// <summary>
    /// Path of the authority's private key in PEM — the one secret in this deployment that cannot be
    /// replaced without a USB visit to every provisioned printer.
    /// </summary>
    public string AuthorityKeyPemPath => Path.Combine(_directory, AuthorityKeyPemFileName);

    /// <summary>Where the leaf is written in PEM, for nginx. See <see cref="LeafCertificatePemFileName"/>.</summary>
    /// <remarks>
    /// In <see cref="CertificateOptions.ProxyDirectory"/> rather than beside the authority, because
    /// the proxy container mounts that directory and must not be handed the authority's private key.
    /// </remarks>
    public string LeafCertificatePemPath => Path.Combine(_proxyDirectory, LeafCertificatePemFileName);

    /// <summary>Where the leaf's private key is written in PEM, for nginx.</summary>
    public string LeafKeyPemPath => Path.Combine(_proxyDirectory, LeafKeyPemFileName);

    private string LegacyAuthorityPath => Path.Combine(_directory, LegacyAuthorityFileName);

    private string LegacyLeafPath => Path.Combine(_directory, LegacyLeafFileName);

    private string Passphrase => _options.AuthorityPassphrase ?? string.Empty;

    /// <summary>
    /// The names a certificate vouches for, as they were written — <c>dNSName</c> entries, including
    /// the ones that are really IP addresses.
    /// </summary>
    /// <remarks>
    /// Reads the SAN rather than the subject on purpose: the subject is decoration here (mbedTLS
    /// consults a CN only when there is no SAN at all), so the SAN is the whole of what a printer will
    /// match against. Step 6's drift detection is the other caller this is shaped for — "is this
    /// machine's address still in the certificate?" is exactly this list.
    /// </remarks>
    /// <param name="certificate">Any certificate; one with no SAN extension yields an empty list.</param>
    public static IReadOnlyList<string> NamesOf(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        X509Extension? extension = certificate.Extensions[SubjectAlternativeNameOid];

        if (extension is null)
        {
            return [];
        }

        return [.. new X509SubjectAlternativeNameExtension(extension.RawData, extension.Critical).EnumerateDnsNames()];
    }

    /// <summary>
    /// Returns the authority with its private key, creating it on first call and loading it every
    /// time after.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Idempotence is the property that matters most in this class.</b> Minting a second authority
    /// would not fail, or log, or look wrong — it would silently stop every already-provisioned
    /// printer from validating, and the only remedy is a USB visit to each one. So the existing key
    /// wins whenever there is one, nothing here rotates anything automatically, and <b>a key that
    /// exists but cannot be read is a refusal to start</b>
    /// (<see cref="CertificateAuthorityUnreadableException"/>), never a fall-through to minting.
    /// </para>
    /// <para>
    /// <b>A passphrase is required, full stop.</b> The key is never minted, loaded or migrated
    /// without one, so there is no plaintext-at-rest mode to configure into by accident — an empty
    /// <see cref="CertificateOptions.AuthorityPassphrase"/> is the first refusal below.
    /// </para>
    /// <para>
    /// Callers that only need to know what the authority <i>says</i> — names, dates — should take
    /// <see cref="LoadAuthorityCertificate"/> instead, which touches no key material and needs no
    /// passphrase.
    /// </para>
    /// </remarks>
    public X509Certificate2 EnsureAuthority()
    {
        if (Passphrase.Length == 0)
        {
            // Before anything touches the disk, so a misconfigured start leaves no trace. This is
            // the only gate on an empty passphrase: everything below may assume one is set.
            throw new CertificateAuthorityUnreadableException(
                "No Certificates:AuthorityPassphrase is configured (CA_PASSPHRASE in .env on the shipped stack; " +
                "setup-env.sh generates one), and the printer authority's private key is never handled without one. " +
                "If a key encrypted under a previous passphrase exists, only that exact value can open it - nothing " +
                "here will mint a replacement, because that would strand every provisioned printer.");
        }

        System.IO.Directory.CreateDirectory(_directory);

        if (File.Exists(AuthorityKeyPemPath))
        {
            if (!File.Exists(AuthorityCertificatePemPath))
            {
                // The certificate is public and connect.der is a byte-for-byte copy of it, so a
                // missing PEM beside a surviving key is repairable rather than fatal.
                if (!File.Exists(AuthorityDerPath))
                {
                    throw new CertificateAuthorityUnreadableException(
                        $"The printer authority's key is in {AuthorityKeyPemPath} but its certificate is gone - neither " +
                        $"{AuthorityCertificatePemPath} nor {AuthorityDerPath} exists. Restore either file from a backup; " +
                        "provisioned printers hold the same certificate, so a fresh authority would strand them all.");
                }

                using X509Certificate2 fromDer = X509CertificateLoader.LoadCertificateFromFile(AuthorityDerPath);

                WriteFile(AuthorityCertificatePemPath, Encoding.ASCII.GetBytes(fromDer.ExportCertificatePem()));
            }

            X509Certificate2 authority = LoadAuthorityPair();

            if (File.Exists(LegacyAuthorityPath))
            {
                // A migration that wrote its PEMs and then died before this line. The pair above is
                // verified readable, so the plaintext PKCS#12 is the one copy too many.
                File.Delete(LegacyAuthorityPath);
            }

            if (!File.Exists(AuthorityDerPath))
            {
                WriteFile(AuthorityDerPath, authority.Export(X509ContentType.Cert));
            }

            return authority;
        }

        if (File.Exists(AuthorityCertificatePemPath))
        {
            throw new CertificateAuthorityUnreadableException(
                $"{AuthorityCertificatePemPath} exists but the private key beside it ({AuthorityKeyPemPath}) is gone. " +
                "Restore the key from a backup; it cannot be recreated, and a fresh authority would strand every " +
                "provisioned printer until each is re-provisioned from a USB stick.");
        }

        if (File.Exists(LegacyAuthorityPath))
        {
            return MigrateAuthorityFromPkcs12();
        }

        return MintAuthority();
    }

    /// <summary>
    /// The authority's certificate — the public half alone — or null if none has been minted yet.
    /// </summary>
    /// <remarks>
    /// For callers that ask what the authority says rather than needing it to sign: the health
    /// check's expiry question is the one this exists for. It reads no key material, so it works
    /// without the passphrase and costs no key derivation — and it never mints, because a health
    /// probe that quietly replaced the authority would be the exact disaster the probe watches for.
    /// </remarks>
    public X509Certificate2? LoadAuthorityCertificate()
    {
        if (File.Exists(AuthorityCertificatePemPath))
        {
            return X509Certificate2.CreateFromPem(File.ReadAllText(AuthorityCertificatePemPath));
        }

        if (File.Exists(AuthorityDerPath))
        {
            return X509CertificateLoader.LoadCertificateFromFile(AuthorityDerPath);
        }

        if (File.Exists(LegacyAuthorityPath))
        {
            // A deployment that has not started on this version yet - readable here, migrated the
            // next time EnsureAuthority runs.
            return X509CertificateLoader.LoadPkcs12FromFile(LegacyAuthorityPath, null, X509KeyStorageFlags.DefaultKeySet);
        }

        return null;
    }

    /// <summary>
    /// Returns the leaf Kestrel serves to printers, issuing it over <paramref name="names"/> on the
    /// first run and loading the same one every run after.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Issued once and then frozen, deliberately.</b> The obvious alternative — reissue whenever
    /// the detected address set changes — was rejected (Henrik, 2026-07-29: <i>"automagically
    /// reissuing cert smells like trouble to me"</i>) for three reasons that all bite in practice.
    /// Interfaces flap: a VPN coming up, <c>docker0</c> appearing, a switch from wifi to ethernet, and
    /// every reissue drops each printer connection as Kestrel picks up the new certificate. It is
    /// non-deterministic: the certificate becomes a function of what the machine happened to look like
    /// at boot, so "what does your certificate say?" cannot be answered without looking. And it is the
    /// server quietly asserting new identities — joining a VPN would silently add that address to the
    /// SANs, which is not a thing that should happen while nobody is watching.
    /// </para>
    /// <para>
    /// So a moved DHCP lease is an operator action, not a self-healing one. That is what leaves drift
    /// detection a real job: notice that this machine's
    /// address is no longer in the certificate and offer the reissue, rather than performing it
    /// unasked. Deleting the leaf's PEM pair is the manual form of the same thing, and costs nothing
    /// at a printer — they trust the authority, not the leaf.
    /// </para>
    /// </remarks>
    /// <param name="names">Names to cover if this is the first run. Ignored once a leaf exists.</param>
    /// <returns>The leaf without its private key, which this process has no use for.</returns>
    public X509Certificate2 EnsureLeaf(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        bool certificateExists = File.Exists(LeafCertificatePemPath);
        bool keyExists = File.Exists(LeafKeyPemPath);

        if (certificateExists && keyExists)
        {
            X509Certificate2 existing = X509Certificate2.CreateFromPem(File.ReadAllText(LeafCertificatePemPath));

            if (File.Exists(LegacyLeafPath))
            {
                // The PEM pair is the leaf now; a PKCS#12 left beside the authority is a second copy
                // of a private key with nothing reading it.
                File.Delete(LegacyLeafPath);
            }

            // Earlier versions wrote the key world-readable; the leaf is not reissued on upgrade, so
            // this is the one place an existing deployment's key gets its mode corrected.
            SetProxyKeyMode(LeafKeyPemPath);

            // At Information because it is the answer to "what must a printer connect to?", and the operator
            // needs it whenever provisioning does not work. It is also what step 6 will compare against.
            _logger.LogInformation("Serving the existing printer certificate for {Names}, valid until {NotAfter:o}. "
                                   + "Delete {CertificatePath} and {KeyPath} to have a new one issued for this "
                                   + "machine's current addresses.",
                                   string.Join(", ", NamesOf(existing)), existing.NotAfter,
                                   LeafCertificatePemPath, LeafKeyPemPath);

            return existing;
        }

        if (File.Exists(LegacyLeafPath))
        {
            return MigrateLeafFromPkcs12(names);
        }

        if (certificateExists || keyExists)
        {
            // Half a pair serves nobody - nginx needs both files - and the leaf is the replaceable
            // kind of secret, so repair by reissuing rather than refusing to start.
            _logger.LogWarning("Only half of the printer leaf PEM pair exists ({CertificatePath} / {KeyPath}), so the "
                               + "proxy could not have served it. Issuing a fresh certificate, which printers accept "
                               + "because they trust the authority rather than the leaf.",
                               LeafCertificatePemPath, LeafKeyPemPath);
        }

        return IssueLeaf(names);
    }

    /// <summary>
    /// The leaf as it stands, or null if none has been issued yet.
    /// </summary>
    /// <remarks>
    /// For callers that need to know what the certificate says without being the reason one exists:
    /// the provisioning bundle asking which names it may write, and step 6's drift detection. Issuing
    /// belongs to <see cref="EnsureLeaf"/> and to startup, where the listener needs it — a page that
    /// minted a certificate as a side effect of being rendered would be a surprising thing.
    /// </remarks>
    public X509Certificate2? LoadLeafIfIssued()
    {
        if (File.Exists(LeafCertificatePemPath))
        {
            return X509Certificate2.CreateFromPem(File.ReadAllText(LeafCertificatePemPath));
        }

        if (File.Exists(LegacyLeafPath))
        {
            // A deployment that has not started on this version yet - passive here, migrated by
            // EnsureLeaf like everything else.
            return X509CertificateLoader.LoadPkcs12FromFile(LegacyLeafPath, null, X509KeyStorageFlags.DefaultKeySet);
        }

        return null;
    }

    /// <summary>
    /// Issues the printer-facing leaf for <paramref name="names"/>, replacing any previous one.
    /// </summary>
    /// <remarks>
    /// Safe to call whenever the names change, precisely because the printer trusts the authority
    /// rather than the leaf: a reissued leaf needs the proxy reloaded and nothing else. Not this
    /// process restarted — it stopped serving the certificate when nginx took over printer TLS, so
    /// what has to re-read the file is the proxy, which does it without dropping the application.
    /// </remarks>
    /// <param name="names">
    /// Every name or address a printer might be told to use. All are written as <b>dNSName</b>
    /// entries — see this class's remarks before changing that.
    /// </param>
    /// <returns>The leaf without its private key, which lives only in the proxy directory.</returns>
    public X509Certificate2 IssueLeaf(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        string[] distinct = names.Where(n => !string.IsNullOrWhiteSpace(n))
                                 .Select(n => n.Trim())
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .ToArray();

        if (distinct.Length == 0)
        {
            throw new ArgumentException("A leaf needs at least one name.", nameof(names));
        }

        using X509Certificate2 authority = EnsureAuthority();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        CertificateRequest request = new($"CN={distinct[0]}", key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
                                              certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0,
                                              critical: true));

        // DigitalSignature is what ECDHE_ECDSA needs; the firmware negotiates nothing else.
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
                                              X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                                              [new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // serverAuth

        SubjectAlternativeNameBuilder subjectNames = new();

        foreach (string name in distinct)
        {
            // AddDnsName even for a dotted quad. AddIpAddress would produce the conformant iPAddress
            // entry, which is exactly the form firmware never matches.
            subjectNames.AddDnsName(name);
        }

        request.CertificateExtensions.Add(subjectNames.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        DateTimeOffset now = _time.GetUtcNow();
        byte[] serial = RandomNumberGenerator.GetBytes(16);

        // The authority is backdated identically: chain building checks its dates too, so
        // backdating only the leaf would fix nothing.
        X509Certificate2 issued = request.Create(
            authority, NotBefore, now.AddDays(_options.LeafValidityDays), serial);

        // The pair goes in the proxy's own directory because that is the one it mounts - see
        // CertificateOptions.ProxyDirectory for why the authority's key must not be in there with
        // them. Deliberately the leaf on its own, with no chain appended - see
        // LeafCertificatePemFileName for why appending the authority breaks verification on the
        // printer.
        System.IO.Directory.CreateDirectory(_proxyDirectory);

        WriteProxyFile(LeafCertificatePemPath, Encoding.ASCII.GetBytes(issued.ExportCertificatePem()));
        WriteProxyKeyFile(LeafKeyPemPath, Encoding.ASCII.GetBytes(key.ExportPkcs8PrivateKeyPem()));

        if (File.Exists(LegacyLeafPath))
        {
            // A reissue that left the old PKCS#12 behind would leave two files disagreeing about
            // what the leaf is, which is the confusion the single-copy layout exists to end.
            File.Delete(LegacyLeafPath);
        }

        _logger.LogInformation("Issued a printer certificate for {Names}, valid until {NotAfter:o}.",
                               string.Join(", ", distinct), issued.NotAfter);

        // After the files, so anything that re-reads the leaf on this signal finds the new one. Here
        // rather than at the call sites because every path that issues comes through here, and the
        // one that forgot to notify would be a name the certificate covers and the host filter
        // refuses until the next restart.
        _leafChanged.NotifyIssued();

        return issued;
    }

    /// <summary>
    /// Loads the PEM pair, encrypting the key file under the configured passphrase if it is not
    /// already.
    /// </summary>
    /// <remarks>
    /// The PEM label says which state the key is in, so nothing here decrypts on speculation — an
    /// encrypted key the passphrase cannot open is a precise refusal naming the fix. A
    /// <i>plaintext</i> key on disk is not an error but the passphrase-rotation path: decrypt the
    /// key by hand (<c>openssl pkcs8</c>), change the configured passphrase, restart, and this
    /// re-encrypts under the new value. The caller has already refused an empty passphrase, so
    /// rotation can never quietly end at "no encryption".
    /// </remarks>
    private X509Certificate2 LoadAuthorityPair()
    {
        bool encrypted = File.ReadAllText(AuthorityKeyPemPath).Contains(EncryptedKeyPemLabel, StringComparison.Ordinal);

        if (encrypted)
        {
            try
            {
                return X509Certificate2.CreateFromEncryptedPemFile(AuthorityCertificatePemPath, Passphrase, AuthorityKeyPemPath);
            }
            catch (CryptographicException exception)
            {
                throw new CertificateAuthorityUnreadableException(
                    $"The configured passphrase does not open the printer authority's key ({AuthorityKeyPemPath}). " +
                    "Restore the Certificates:AuthorityPassphrase value this key was encrypted with (CA_PASSPHRASE in " +
                    ".env on the shipped stack) - a changed or retyped value cannot open it, and nothing here will " +
                    "mint a replacement, because that would strand every provisioned printer.", exception);
            }
        }

        X509Certificate2? authority = null;

        try
        {
            try
            {
                authority = X509Certificate2.CreateFromPemFile(AuthorityCertificatePemPath, AuthorityKeyPemPath);
            }
            catch (CryptographicException exception)
            {
                throw new CertificateAuthorityUnreadableException(
                    $"The printer authority's PEM pair ({AuthorityCertificatePemPath}, {AuthorityKeyPemPath}) cannot be " +
                    "read - a file is damaged, or the key does not belong to the certificate. Restore both from the same " +
                    "backup; nothing here will mint a replacement, because that would strand every provisioned printer.",
                    exception);
            }

            X509Certificate2 reEncrypted;

            using (ECDsa key = authority.GetECDsaPrivateKey()!)
            {
                // The encrypted pair, not the plaintext one just loaded: they are the same
                // certificate, and this one is the copy proved to open under the configured
                // passphrase - which is the question this arm exists to answer.
                reEncrypted = WriteAuthorityKey(key);
            }

            _logger.LogInformation("Encrypted the printer authority's private key ({Path}) with the configured "
                                   + "passphrase. From now on that passphrase is required to read the key - keep it "
                                   + "backed up as carefully as the key itself, and separately from the data "
                                   + "directory.", AuthorityKeyPemPath);

            return reEncrypted;
        }
        finally
        {
            authority?.Dispose();
        }
    }

    /// <summary>
    /// Writes the authority's key in PEM, encrypted under the configured passphrase, proves the
    /// result opens before it replaces anything, and hands back what opening it produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written to a sibling temp file, round-tripped against the certificate, then moved over the
    /// real name. The verification is what makes the callers' deletions safe: nothing that removes an
    /// older copy of this key runs until a readable replacement is on disk, and the rename means no
    /// crash leaves a half-written key under the real name.
    /// </para>
    /// <para>
    /// <b>Returning the verified pair is what keeps a mint to one key derivation.</b> Opening an
    /// encrypted key costs a deliberately expensive PBKDF2 pass — the whole point of the work factor —
    /// and every caller here needs exactly what this verification already built. Each used to throw it
    /// away and read the file a second time, so a first boot paid the derivation twice for one
    /// certificate. The pair is the file's, not the caller's in-memory copy, so nothing is taken on
    /// trust that was not read back from disk.
    /// </para>
    /// </remarks>
    private X509Certificate2 WriteAuthorityKey(ECDsa key)
    {
        string pem = key.ExportEncryptedPkcs8PrivateKeyPem(Passphrase, KeyEncryption);

        string temporary = AuthorityKeyPemPath + ".tmp";

        WriteFile(temporary, Encoding.ASCII.GetBytes(pem));

        X509Certificate2? verified = null;

        try
        {
            verified = X509Certificate2.CreateFromEncryptedPemFile(
                AuthorityCertificatePemPath, Passphrase, temporary);

            using (ECDsa? verifiedKey = verified.GetECDsaPrivateKey())
            {
                if (verifiedKey is null)
                {
                    throw new CertificateAuthorityUnreadableException(
                        $"The freshly written authority key ({temporary}) failed verification, so nothing was replaced.");
                }
            }

            File.Move(temporary, AuthorityKeyPemPath, overwrite: true);

            X509Certificate2 proven = verified;

            verified = null;

            return proven;
        }
        finally
        {
            verified?.Dispose();
        }
    }

    /// <summary>
    /// Moves an authority written by an earlier version — a passwordless PKCS#12 — to the PEM pair,
    /// then deletes the PKCS#12.
    /// </summary>
    /// <remarks>
    /// The delete is the point of the exercise: the PKCS#12 holds the key in the clear, and it is
    /// only removed after the PEM pair has been written and read back under the passphrase — which
    /// <see cref="WriteAuthorityKey"/> does as its verification, and hands back, so the pair returned
    /// here came off disk rather than out of the PKCS#12. A crash anywhere in between leaves both
    /// layouts on disk, and the next start finishes the job from the top of
    /// <see cref="EnsureAuthority"/>.
    /// </remarks>
    private X509Certificate2 MigrateAuthorityFromPkcs12()
    {
        X509Certificate2 migrated;
        X509Certificate2 legacy;

        try
        {
            legacy = X509CertificateLoader.LoadPkcs12FromFile(LegacyAuthorityPath, null, X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException exception)
        {
            throw new CertificateAuthorityUnreadableException(
                $"The printer authority ({LegacyAuthorityPath}) cannot be read. Restore it from a backup; nothing " +
                "here will mint a replacement, because that would strand every provisioned printer.", exception);
        }

        using (legacy)
        {
            using ECDsa key = legacy.GetECDsaPrivateKey() ??
                throw new CertificateAuthorityUnreadableException(
                    $"The printer authority ({LegacyAuthorityPath}) carries no ECDSA private key, so it cannot sign " +
                    "anything and cannot be migrated. Restore it from a backup; nothing here will mint a replacement, " +
                    "because that would strand every provisioned printer.");

            WriteFile(AuthorityCertificatePemPath, Encoding.ASCII.GetBytes(legacy.ExportCertificatePem()));

            migrated = WriteAuthorityKey(key);
        }

        File.Delete(LegacyAuthorityPath);

        _logger.LogInformation("Moved the printer authority from {Legacy} to {CertificatePath} and {KeyPath}. The "
                               + "authority itself is unchanged - no printer notices - and the key file is now "
                               + "encrypted with the configured passphrase.",
                               LegacyAuthorityPath, AuthorityCertificatePemPath, AuthorityKeyPemPath);

        return migrated;
    }

    /// <summary>
    /// Moves a leaf written by an earlier version — a passwordless PKCS#12 beside the authority — to
    /// the proxy directory's PEM pair, then deletes the PKCS#12.
    /// </summary>
    /// <remarks>
    /// This is also the path that upgrades a deployment issued a certificate before nginx terminated
    /// printer TLS: such a deployment has the PKCS#12 and nothing in the proxy directory, and without
    /// this the proxy would decline to serve the printer listener at all — every printer stops
    /// connecting, and the only diagnostic is a line in the proxy's log saying the leaf is missing,
    /// which reads as "PrinterTls must be off". Migrated rather than reissued so an upgrade never
    /// silently rolls the leaf or loses names the operator had deliberately covered.
    /// </remarks>
    private X509Certificate2 MigrateLeafFromPkcs12(IEnumerable<string> names)
    {
        using X509Certificate2 legacy = X509CertificateLoader.LoadPkcs12FromFile(
            LegacyLeafPath, null, X509KeyStorageFlags.Exportable);
        using ECDsa? key = legacy.GetECDsaPrivateKey();

        if (key is null)
        {
            // Would mean a PKCS#12 written by something other than this class, since everything here
            // is ECDSA P-256 by firmware necessity. A leaf nobody holds the key to serves nothing, so
            // repair by reissuing - free at the printers, which trust the authority.
            _logger.LogWarning("The legacy printer certificate ({Path}) carries no ECDSA private key, so it cannot be "
                               + "served and cannot be migrated. Issuing a fresh certificate in its place.",
                               LegacyLeafPath);
            File.Delete(LegacyLeafPath);

            return IssueLeaf(names);
        }

        System.IO.Directory.CreateDirectory(_proxyDirectory);

        WriteProxyFile(LeafCertificatePemPath, Encoding.ASCII.GetBytes(legacy.ExportCertificatePem()));
        WriteProxyKeyFile(LeafKeyPemPath, Encoding.ASCII.GetBytes(key.ExportPkcs8PrivateKeyPem()));

        File.Delete(LegacyLeafPath);

        _logger.LogInformation("Moved the printer certificate from {Legacy} to {CertificatePath} and {KeyPath}, which "
                               + "are what the proxy serves. The certificate itself is unchanged.",
                               LegacyLeafPath, LeafCertificatePemPath, LeafKeyPemPath);

        return X509Certificate2.CreateFromPem(File.ReadAllText(LeafCertificatePemPath));
    }

    /// <summary>
    /// Mints the authority: the one-time event every provisioned printer's trust descends from.
    /// </summary>
    private X509Certificate2 MintAuthority()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        CertificateRequest request = new($"CN={_options.AuthorityName}", key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
                                              certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0,
                                              critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
                                              X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        DateTimeOffset now = _time.GetUtcNow();
        using X509Certificate2 authority = request.CreateSelfSigned(
            NotBefore, now.AddDays(_options.AuthorityValidityDays));

        WriteFile(AuthorityCertificatePemPath, Encoding.ASCII.GetBytes(authority.ExportCertificatePem()));
        WriteFile(AuthorityDerPath, authority.Export(X509ContentType.Cert));

        // The DER moved above the key deliberately: the pair this returns is the one the write
        // verified, so there is no re-read afterwards to hang the remaining writes off. A crash
        // between the certificate and the key lands on the same refusal it always did - the
        // certificate exists and its key does not - and the DER being there too does not change which.
        X509Certificate2 minted = WriteAuthorityKey(key);

        _logger.LogWarning("Minted a new printer certificate authority in {Directory}. Every printer provisioned "
                           + "from a previous authority will no longer validate this server and must be "
                           + "re-provisioned from a USB stick.", _directory);

        return minted;
    }

    /// <summary>
    /// Writes a key-bearing file as close to owner-only as the platform allows.
    /// </summary>
    /// <remarks>
    /// Best-effort rather than guaranteed: <c>File.SetUnixFileMode</c> does nothing on Windows, and
    /// the containerised deployment runs as a single user anyway. It is worth doing because the
    /// alternative is a CA private key inheriting whatever the directory default happens to be.
    /// </remarks>
    private static void WriteFile(string path, byte[] contents)
    {
        File.WriteAllBytes(path, contents);

        if (!OperatingSystem.IsWindows() && !path.EndsWith(".der", StringComparison.OrdinalIgnoreCase))
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    /// Writes a file the proxy container has to be able to read.
    /// </summary>
    /// <remarks>
    /// <b>Wider than <see cref="WriteFile"/>, deliberately.</b> nginx runs as its own uid in its own
    /// container and cannot read a file this process wrote owner-only, so the alternative to widening
    /// these is a proxy that starts, finds a key it may not open, and reports a certificate problem
    /// that has nothing to do with the certificate. The certificate is public material and stays
    /// world-readable; the key is group-readable only - compose adds this process's group to the
    /// proxy container, which is the whole of what the group grant is for. What is widened is bounded
    /// twice over: to a volume that only these two containers mount, and to the leaf, which is
    /// replaceable without visiting a printer. <see cref="CertificateOptions.ProxyDirectory"/> carries
    /// the full argument, including why the authority's key is not in the same directory.
    /// </remarks>
    private static void WriteProxyFile(string path, byte[] contents)
    {
        File.WriteAllBytes(path, contents);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                                 UnixFileMode.UserRead | UnixFileMode.UserWrite
                                                       | UnixFileMode.GroupRead
                                                       | UnixFileMode.OtherRead);
        }
    }

    /// <summary>
    /// Writes the leaf's private key so this process and the proxy's group can read it, and nobody
    /// else. See <see cref="WriteProxyFile"/> for why the proxy reads it at all.
    /// </summary>
    private static void WriteProxyKeyFile(string path, byte[] contents)
    {
        File.WriteAllBytes(path, contents);

        SetProxyKeyMode(path);
    }

    /// <summary>
    /// Owner read-write, group read - the mode <see cref="WriteProxyKeyFile"/> writes, re-assertable
    /// on a key an earlier version left world-readable.
    /// </summary>
    private static void SetProxyKeyMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }
    }
}
