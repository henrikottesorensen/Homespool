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
/// names carried as <b>dNSName</b> SAN entries even when they are IP addresses. See
/// <c>notes/tls-by-default.md</c>.
/// </para>
/// <para>
/// <b>The IP-as-dNSName encoding is the load-bearing oddity</b>, and it is deliberate rather than
/// sloppy. mbedTLS matches SAN entries by plain string comparison and never parses them as addresses,
/// so <c>192.168.1.50</c> in a dNSName entry matches a <c>hostname</c> of <c>192.168.1.50</c>. The
/// RFC-correct form — an iPAddress entry — reaches firmware's "unrecognised type" arm and is
/// <i>never</i> matched. Confirmed on an MK3.5 on 2026-07-29. Do not "fix" this to use
/// <c>AddIpAddress</c>; that is the form that fails.
/// </para>
/// </remarks>
public class PrinterCertificateAuthority
{
    private const string AuthorityFileName = "ca.pfx";
    private const string AuthorityDerFileName = "connect.der";
    private const string LeafFileName = "printer.pfx";

    /// <summary>
    /// The leaf and its key in PEM, for a TLS terminator that cannot read PKCS#12 - nginx, which
    /// terminates the printer connection as of 2026-07-31.
    /// </summary>
    /// <remarks>
    /// <b>The certificate file holds the leaf alone, with no chain appended, and that is
    /// load-bearing.</b> Firmware's <c>x509_crt_check_ee_locally_trusted</c> requires exactly one
    /// certificate to be presented; a terminator that sends leaf + authority fails verification in a
    /// way that reads as a protocol bug rather than a certificate one
    /// (<c>notes/tls-by-default.md</c>, decision 3a).
    /// </remarks>
    private const string LeafCertificatePemFileName = "printer-leaf.pem";

    private const string LeafKeyPemFileName = "printer-leaf.key.pem";
    private const string SubjectAlternativeNameOid = "2.5.29.17";

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

    public PrinterCertificateAuthority(IOptions<CertificateOptions> options,
                                       IHostEnvironmentAccessor environment,
                                       TimeProvider time,
                                       ILogger<PrinterCertificateAuthority> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        _options = options.Value;
        _time = time;
        _logger = logger;
        _directory = Path.IsPathRooted(_options.Directory)
            ? _options.Directory
            : Path.Combine(environment.ContentRootPath, _options.Directory);

        _proxyDirectory = Path.IsPathRooted(_options.ProxyDirectory)
            ? _options.ProxyDirectory
            : Path.Combine(environment.ContentRootPath, _options.ProxyDirectory);
    }

    /// <summary>Path of the DER-encoded authority, which is what goes on the USB stick.</summary>
    public string AuthorityDerPath => Path.Combine(_directory, AuthorityDerFileName);

    /// <summary>Path of the leaf, as a PFX Kestrel can be pointed at.</summary>
    public string LeafPath => Path.Combine(_directory, LeafFileName);

    /// <summary>Where the leaf is written in PEM, for nginx. See <see cref="LeafCertificatePemFileName"/>.</summary>
    /// <remarks>
    /// In <see cref="CertificateOptions.ProxyDirectory"/> rather than beside the PKCS#12, because the
    /// proxy container mounts that directory and must not be handed the authority's private key.
    /// </remarks>
    public string LeafCertificatePemPath => Path.Combine(_proxyDirectory, LeafCertificatePemFileName);

    /// <summary>Where the leaf's private key is written in PEM, for nginx.</summary>
    public string LeafKeyPemPath => Path.Combine(_proxyDirectory, LeafKeyPemFileName);

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
    /// Returns the authority, creating it on first call and loading it every time after.
    /// </summary>
    /// <remarks>
    /// <b>Idempotence is the property that matters most in this class.</b> Minting a second authority
    /// would not fail, or log, or look wrong — it would silently stop every already-provisioned
    /// printer from validating, and the only remedy is a USB visit to each one. So the existing file
    /// wins whenever there is one, and nothing here rotates anything automatically.
    /// </remarks>
    public X509Certificate2 EnsureAuthority()
    {
        System.IO.Directory.CreateDirectory(_directory);

        string path = Path.Combine(_directory, AuthorityFileName);

        if (File.Exists(path))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.Exportable);
        }

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        CertificateRequest request = new($"CN={_options.AuthorityName}", key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        DateTimeOffset now = _time.GetUtcNow();
        using X509Certificate2 authority = request.CreateSelfSigned(
            NotBefore, now.AddDays(_options.AuthorityValidityDays));

        WriteFile(path, authority.Export(X509ContentType.Pkcs12));
        WriteFile(AuthorityDerPath, authority.Export(X509ContentType.Cert));

        _logger.LogWarning("Minted a new printer certificate authority in {Directory}. Every printer provisioned "
                           + "from a previous authority will no longer validate this server and must be "
                           + "re-provisioned from a USB stick.", _directory);

        return X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.Exportable);
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
    /// detection a real job (<c>notes/tls-by-default.md</c> step 6): notice that this machine's
    /// address is no longer in the certificate and offer the reissue, rather than performing it
    /// unasked. Deleting <c>printer.pfx</c> is the manual form of the same thing, and costs nothing at
    /// a printer — they trust the authority, not the leaf.
    /// </para>
    /// </remarks>
    /// <param name="names">Names to cover if this is the first run. Ignored once a leaf exists.</param>
    public X509Certificate2 EnsureLeaf(IEnumerable<string> names)
    {
        if (!File.Exists(LeafPath))
        {
            return IssueLeaf(names);
        }

        X509Certificate2 existing = X509CertificateLoader.LoadPkcs12FromFile(LeafPath, null, X509KeyStorageFlags.Exportable);

        // The PEM copies, if this leaf predates them. Cheap to check and load-bearing on exactly one
        // path: upgrading a deployment that was issued a certificate before nginx terminated printer
        // TLS. Such a deployment has printer.pfx and nothing in the proxy directory, so without this
        // the method above returns early, no PEM is ever written, and the proxy declines to serve the
        // printer listener at all - every printer stops connecting, and the only diagnostic is a line
        // in the proxy's log saying the leaf is missing, which reads as "PrinterTls must be off".
        //
        // Exported from the certificate already loaded rather than reissued: printers trust the
        // authority, so a reissue would work, but it would silently roll the leaf on every upgrade
        // and lose whatever names the operator had deliberately covered.
        EnsureProxyPem(existing);

        // At Information because it is the answer to "what must a printer connect to?", and the operator
        // needs it whenever provisioning does not work. It is also what step 6 will compare against.
        _logger.LogInformation("Serving the existing printer certificate for {Names}, valid until {NotAfter:o}. "
                               + "Delete {Path} to have a new one issued for this machine's current addresses.",
                               string.Join(", ", NamesOf(existing)), existing.NotAfter, LeafPath);

        return existing;
    }

    /// <summary>
    /// Writes the PEM pair the proxy reads, if it is not already beside the PKCS#12.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The upgrade path, and nothing else.</b> A leaf issued by any earlier version of this
    /// deployment exists only as <c>printer.pfx</c>; nginx cannot read PKCS#12, so a stack upgraded
    /// without this has a proxy with no certificate to present and printers that cannot connect. It is
    /// idempotent and costs two <c>File.Exists</c> calls on every other start.
    /// </para>
    /// <para>
    /// The certificate file gets the leaf <i>alone</i>, as everywhere else here — firmware requires
    /// exactly one certificate presented.
    /// </para>
    /// </remarks>
    private void EnsureProxyPem(X509Certificate2 leaf)
    {
        if (File.Exists(LeafCertificatePemPath) && File.Exists(LeafKeyPemPath))
        {
            return;
        }

        using ECDsa? key = leaf.GetECDsaPrivateKey();

        if (key is null)
        {
            // Would mean a PKCS#12 written by something other than this class, since everything here
            // is ECDSA P-256 by firmware necessity. Report it rather than throwing on the startup
            // path: the pages still work, and the message names the fix.
            _logger.LogError("The printer certificate in {Path} carries no ECDSA private key, so the PEM copies the "
                             + "proxy needs cannot be written and printers will not be able to connect. Delete that "
                             + "file and restart to have a new certificate issued.", LeafPath);

            return;
        }

        System.IO.Directory.CreateDirectory(_proxyDirectory);

        WriteProxyFile(LeafCertificatePemPath, Encoding.ASCII.GetBytes(leaf.ExportCertificatePem()));
        WriteProxyFile(LeafKeyPemPath, Encoding.ASCII.GetBytes(key.ExportPkcs8PrivateKeyPem()));

        _logger.LogInformation("Wrote the existing printer certificate to {Path} in PEM for the proxy, which reads "
                               + "that rather than the PKCS#12 beside it.", LeafCertificatePemPath);
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
        return File.Exists(LeafPath)
            ? X509CertificateLoader.LoadPkcs12FromFile(LeafPath, null, X509KeyStorageFlags.Exportable)
            : null;
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
            certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));

        // DigitalSignature is what ECDHE_ECDSA needs; the firmware negotiates nothing else.
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false));   // serverAuth

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
        using X509Certificate2 issued = request.Create(
            authority, NotBefore, now.AddDays(_options.LeafValidityDays), serial);

        using X509Certificate2 withKey = issued.CopyWithPrivateKey(key);

        WriteFile(LeafPath, withKey.Export(X509ContentType.Pkcs12));

        // PEM as well as PKCS#12, because nginx reads one and not the other, and in their own
        // directory because that is the one the proxy mounts - see CertificateOptions.ProxyDirectory
        // for why the authority's key must not be in there with them. Deliberately the leaf on its
        // own, with no chain appended - see LeafCertificatePemFileName for why appending the
        // authority breaks verification on the printer.
        System.IO.Directory.CreateDirectory(_proxyDirectory);

        WriteProxyFile(LeafCertificatePemPath, Encoding.ASCII.GetBytes(issued.ExportCertificatePem()));
        WriteProxyFile(LeafKeyPemPath, Encoding.ASCII.GetBytes(key.ExportPkcs8PrivateKeyPem()));

        _logger.LogInformation("Issued a printer certificate for {Names}, valid until {NotAfter:o}.",
                               string.Join(", ", distinct), issued.NotAfter);

        return X509CertificateLoader.LoadPkcs12FromFile(LeafPath, null, X509KeyStorageFlags.Exportable);
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
    /// <b>Readable by everyone, which <see cref="WriteFile"/> deliberately is not.</b> nginx runs as
    /// its own uid in its own container and cannot read a file this process wrote owner-only, so the
    /// alternative to widening these two is a proxy that starts, finds a key it may not open, and
    /// reports a certificate problem that has nothing to do with the certificate. What is widened is
    /// bounded twice over: to a volume that only these two containers mount, and to the leaf, which is
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
}
