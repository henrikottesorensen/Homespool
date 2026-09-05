using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

using Homespool.Host.Certificates;
using Homespool.Host.Localisation;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Builds the zip an operator unpacks onto a USB stick: the printer's ini, and the certificate
/// authority it must trust.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every failure of the afternoon printer TLS first worked by hand was a hand-assembly failure</b>
/// — a <c>;</c> comment, an omitted key, a PEM renamed <c>.der</c>, a mis-transcribed claim code — and
/// not one was a protocol problem. Each becomes
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
    private readonly CertificateOptions _certificates;
    private readonly PrinterCertificateAuthority _authority;
    private readonly IHostAddressResolver _resolver;

    /// <summary>
    /// Reads the ini's comments and the README in the culture of whoever asked for the bundle.
    /// </summary>
    /// <remarks>
    /// <b>Safe to hold in a singleton</b>, because a localiser resolves against the ambient culture
    /// on each call rather than at construction - so one instance answers every request in that
    /// request's own language.
    /// </remarks>
    private readonly IStringLocalizer<SharedResource> _localiser;

    public ProvisioningBundleBuilder(IOptionsMonitor<PrusaConnectOptions> options,
                                     IOptions<CertificateOptions> certificates,
                                     PrinterCertificateAuthority authority,
                                     IHostAddressResolver resolver,
                                     IStringLocalizer<SharedResource> localiser)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(certificates);

        _localiser = localiser;
        _options = options.CurrentValue;
        _certificates = certificates.Value;
        _authority = authority;
        _resolver = resolver;
    }

    /// <summary>
    /// Whether a name is one no printer could use, on the evidence of what it resolves to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Resolved rather than guessed.</b> A container's own address is recognisable on sight
    /// (<see cref="PrinterAddressSuggestion.IsProbablyTheContainersOwn"/>); a container's own
    /// <i>hostname</i> is not - <c>71e04654da9b</c> is a name like any other, and a rule that dropped
    /// names looking like hex would eventually drop somebody's real machine. Asking what it resolves
    /// to answers the question that actually matters instead of the one that is easy to ask.
    /// </para>
    /// <para>
    /// <b>Only a positive answer counts.</b> Nothing resolved means the resolver could not say, not
    /// that the name is bad - a LAN name may well be unresolvable from inside a container while
    /// working perfectly from the printer's side of the network. So an unresolvable name stays on the
    /// list, and only a name that resolves to nothing a printer could use comes off it.
    /// </para>
    /// <para>
    /// <b>Stated as "nothing usable", not "everything unusable"</b>, and the difference is not
    /// academic: resolving a name returns whatever the platform feels like including - an IPv6 entry,
    /// a loopback entry - and a rule asking whether <i>every</i> answer was container-private is
    /// satisfied by none of them. Measured, on the deployment this was written for: the container's own
    /// hostname sailed through the first version of this check.
    /// </para>
    /// </remarks>
    /// <param name="resolved">What the name resolved to; empty means the resolver had no answer.</param>
    /// <param name="containerNetworks">Ranges the deployment says exist only inside itself.</param>
    public static bool IsUnreachableByPrinters(IReadOnlyList<IPAddress> resolved,
                                               IReadOnlyList<IPNetwork> containerNetworks)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        return resolved.Count > 0 && !resolved.Any(address => CouldReachAPrinter(address, containerNetworks));
    }

    /// <summary>
    /// Whether an address is one a printer on the household LAN could actually reach.
    /// </summary>
    /// <remarks>
    /// Everything a printer cannot use, for the same reason: IPv6 because the firmware's stack does
    /// not, loopback and link-local because they name this machine or a failed DHCP lease, and the
    /// container ranges because they exist only inside Docker.
    /// </remarks>
    public static bool CouldReachAPrinter(IPAddress address, IReadOnlyList<IPNetwork> containerNetworks)
    {
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
               && !IPAddress.IsLoopback(address)
               && !address.GetAddressBytes().Take(2).SequenceEqual<byte>([169, 254])
               && !PrinterAddressSuggestion.IsProbablyTheContainersOwn(address, containerNetworks);
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
    public async Task<IReadOnlyList<PrinterAddressSuggestion>> AvailableNamesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<IPNetwork> containerNetworks = _certificates.ParsedContainerNetworks;

        if (!_options.PrinterTls)
        {
            return _options.IsPrinterAddressConfigured ?
                [PrinterAddressSuggestion.Describe(_options.PrinterHost.Trim(), containerNetworks)] :
                [];
        }

        using X509Certificate2? leaf = _authority.LoadLeafIfIssued();

        if (leaf is null)
        {
            return [];
        }

        List<PrinterAddressSuggestion> usable = [];

        foreach (string name in PrinterCertificateAuthority.NamesOf(leaf))
        {
            // Offering an address only the container can reach is not a warning worth writing, it is a
            // choice worth removing: it looks as reasonable as the others, it is the one a Compose
            // deployment volunteers, and picking it produces a bundle that cannot work.
            if (!IsUnreachableByPrinters(await _resolver.ResolveAsync(name, cancellationToken), containerNetworks))
            {
                // Described, not just listed. Whether this name survives a moved DHCP lease is the
                // whole of the choice being made here, and it is knowledge this code already has.
                usable.Add(PrinterAddressSuggestion.Describe(name, containerNetworks));
            }
        }

        // The configured address first when the certificate carries it: it is the one an operator
        // chose deliberately, and the one every other page already talks about.
        return
        [
            .. usable.OrderByDescending(suggestion =>
                                            suggestion.Value.Equals(_options.PrinterHost?.Trim(),
                                                                    StringComparison.OrdinalIgnoreCase))
        ];
    }

    /// <summary>
    /// Builds the bundle for <paramref name="hostname"/>, carrying <paramref name="token"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The name is not one the certificate vouches for. Refused rather than written, because the
    /// failure it would cause happens at a printer, days later, and says only "TLS error".
    /// </exception>
    /// <param name="hostname">The address to write into the ini.</param>
    /// <param name="token">The provisioning token.</param>
    /// <param name="printerName">Named in the instructions, so a folder of these can be told apart.</param>
    /// <param name="cancellationToken">The usual.</param>
    public async Task<byte[]> BuildAsync(string hostname,
                                         string token,
                                         string? printerName,
                                         CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        string name = hostname.Trim();

        // Before the certificate check, and independent of it: the bundle page can address a bundle
        // to a kept name that never went through the options validation, and a name the printer
        // truncates fails whether or not the certificate covers it.
        if (PrinterHostLengthValidator.Refusal(name) is string tooLong)
        {
            throw new ArgumentException(tooLong, nameof(hostname));
        }

        if (_options.PrinterTls
            && !(await AvailableNamesAsync(cancellationToken))
                .Any(suggestion => suggestion.Value.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"'{name}' is not an address a printer could use to reach this server - either the certificate "
                + "does not cover it, or it resolves only inside this container. Choose one of the names offered, "
                + "or reissue the certificate.",
                nameof(hostname));
        }

        using MemoryStream stream = new();

        await using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // UTF-8 with no BOM, and LF endings. A BOM ahead of the first section header is not a
            // comment to an ini parser, it is three bytes of rubbish before '[' - the same class of
            // silent, unexplained parse failure that generating this file exists to remove.
            string ini = ConnectIni.BuildFile(_options, name, token, _localiser).ReplaceLineEndings("\n");

            WriteEntry(archive, ConnectIni.FileName, new UTF8Encoding(false).GetBytes(ini));

            // Read by a person, at the printer, long after the page that produced it is gone - which is
            // where the two things most likely to go wrong are decided: the files belong at the root of
            // the stick, and custom_cert takes this printer away from Prusa Connect until it is undone.
            WriteEntry(archive,
                       ProvisioningReadme.FileNameFor(_localiser),
                       new UTF8Encoding(false).GetBytes(
                           ProvisioningReadme.Build(_options, name, printerName, _localiser).ReplaceLineEndings("\n")));

            // No anchor when nothing is verified: with tls off the ini says custom_cert = 0, and a der
            // beside it would be a file the printer never opens and the operator has to wonder about.
            if (_options.PrinterTls)
            {
                WriteEntry(archive, AuthorityFileName,
                           await File.ReadAllBytesAsync(_authority.AuthorityDerPath, cancellationToken));
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
