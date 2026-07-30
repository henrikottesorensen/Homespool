using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Homespool.Host.Certificates;
using Microsoft.Extensions.Options;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Builds the zip an operator unpacks onto a USB stick: the printer's ini, and the certificate
/// authority it must trust.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every failure of the afternoon printer TLS first worked by hand was a hand-assembly failure</b>
/// — a <c>;</c> comment, an omitted key, a PEM renamed <c>.der</c>, a mis-transcribed claim code — and
/// not one was a protocol problem (<c>notes/usb-provisioning-bundle.md</c>). Each becomes
/// unrepresentable when the server writes the files instead of describing them. The snippet asked a
/// person to be a careful compiler; this asks them to unzip.
/// </para>
/// <para>
/// <b>Both entries sit at the zip root.</b> A wrapping folder would put them one level deep on the
/// stick, where the printer finds neither and says nothing.
/// </para>
/// </remarks>
public sealed class ProvisioningBundleBuilder
{
    /// <summary>
    /// What the firmware expects the trust anchor to be called. Not negotiable and not configurable.
    /// </summary>
    public const string AuthorityFileName = "connect.der";

    private readonly PrusaConnectOptions _options;
    private readonly PrinterCertificateAuthority _authority;

    public ProvisioningBundleBuilder(IOptions<PrusaConnectOptions> options, PrinterCertificateAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _authority = authority;
    }

    /// <summary>
    /// The addresses a bundle may be written for, best first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read from the certificate, not from the network</b>, and that is the whole point. The leaf is
    /// issued once and then frozen (see <see cref="PrinterCertificateAuthority.EnsureLeaf"/>), so what
    /// this machine can see today and what its certificate vouches for can differ — and a bundle
    /// written for an address the certificate does not carry produces a printer that cannot complete a
    /// handshake, reported on its screen as a bare TLS error.
    /// </para>
    /// <para>
    /// With TLS off there is no certificate and nothing to disagree with, so the configured address is
    /// the only answer there is.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> AvailableNames()
    {
        if (!_options.PrinterTls)
        {
            return _options.IsPrinterAddressConfigured ? [_options.PrinterHost.Trim()] : [];
        }

        using X509Certificate2? leaf = _authority.LoadLeafIfIssued();

        if (leaf is null)
        {
            return [];
        }

        IReadOnlyList<string> names = PrinterCertificateAuthority.NamesOf(leaf);

        // The configured address first when the certificate carries it: it is the one an operator
        // chose deliberately, and the one every other page already talks about.
        return [.. names.OrderByDescending(name => name.Equals(_options.PrinterHost?.Trim(), StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// Builds the bundle for <paramref name="hostname"/>, carrying <paramref name="token"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The name is not one the certificate vouches for. Refused rather than written, because the
    /// failure it would cause happens at a printer, days later, and says only "TLS error".
    /// </exception>
    public byte[] Build(string hostname, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        string name = hostname.Trim();

        if (_options.PrinterTls && !AvailableNames().Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The printer certificate does not cover '{name}', so a printer given this bundle could not "
                + "verify this server. Choose one of the names the certificate carries, or reissue it.",
                nameof(hostname));
        }

        using MemoryStream stream = new();

        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // UTF-8 with no BOM, and LF endings. A BOM ahead of the first section header is not a
            // comment to an ini parser, it is three bytes of rubbish before '[' - the same class of
            // silent, unexplained parse failure that generating this file exists to remove.
            string ini = ConnectIni.BuildFile(_options, name, token).ReplaceLineEndings("\n");

            WriteEntry(archive, ConnectIni.FileName, new UTF8Encoding(false).GetBytes(ini));

            // No anchor when nothing is verified: with tls off the ini says custom_cert = 0, and a der
            // beside it would be a file the printer never opens and the operator has to wonder about.
            if (_options.PrinterTls)
            {
                WriteEntry(archive, AuthorityFileName, File.ReadAllBytes(_authority.AuthorityDerPath));
            }
        }

        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] contents)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);

        using Stream target = entry.Open();

        target.Write(contents);
    }
}
