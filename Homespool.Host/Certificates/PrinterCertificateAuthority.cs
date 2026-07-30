using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

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
    private readonly CertificateOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PrinterCertificateAuthority> _logger;

    public PrinterCertificateAuthority(IOptions<CertificateOptions> options,
                                       PrusaConnect.Transfers.IHostEnvironmentAccessor environment,
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
    }

    /// <summary>Path of the DER-encoded authority, which is what goes on the USB stick.</summary>
    public string AuthorityDerPath => Path.Combine(_directory, AuthorityDerFileName);

    /// <summary>Path of the leaf, as a PFX Kestrel can be pointed at.</summary>
    public string LeafPath => Path.Combine(_directory, LeafFileName);

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
    /// Issues the printer-facing leaf for <paramref name="names"/>, replacing any previous one.
    /// </summary>
    /// <remarks>
    /// Safe to call whenever the names change, precisely because the printer trusts the authority
    /// rather than the leaf: a reissued leaf needs a server restart and nothing else.
    /// </remarks>
    /// <param name="names">
    /// Every name or address a printer might be told to dial. All are written as <b>dNSName</b>
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
}
