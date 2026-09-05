using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

using Homespool.Host.Certificates;
using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Listeners;

/// <summary>
/// Lets the host filter answer every name the printer certificate vouches for.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule: anything the printer certificate vouches for is a host a printer may address.</b> A
/// printer dials whatever its ini names and sends that as <c>Host</c>, and the framework's host filter
/// answers 400 to a name it was not told about — before any of this application runs, with a generic
/// HTML page the printer's panel reports as <c>Bug</c>. The list it is told about is composed in
/// <c>compose.yaml</c> from the names people browse to and the one configured printer host, so a
/// printer provisioned for this machine's bare address, which the certificate covered and the bundle
/// page offered, was refused by exactly this. The leaf is issued from the same detection that offers
/// the bundle its addresses, so its names are by construction the complete set a printer may have been
/// given — and deriving the allowed list from it means the two cannot drift.
/// </para>
/// <para>
/// <b>Appended, never replaced.</b> The configured list stays as the floor: it is what people browse
/// to and what the health check curls, and the certificate knows about neither.
/// </para>
/// <para>
/// <b>The configured host joins verbatim, leaf or no leaf.</b> With <c>PrusaConnect:PrinterTls</c> off
/// there is no leaf, and the one name a plaintext printer was told is the configured one. Nothing here
/// resolves it: with TLS on, a name outside the leaf fails the handshake before a <c>Host</c> header is
/// ever read, so the leaf already is the whole set — and this runs synchronously, at the first request
/// and on every reissue, where a resolver's timeout would be paid in full.
/// </para>
/// <para>
/// <b>Ordering matters, and the end-to-end test is what asserts it.</b> The framework's own
/// post-configure fills the list from configuration only while it is still empty, so this one has to
/// run after it — which it does by being registered later. Registered earlier, the configured names
/// would never be read at all. That same framework step leaves an <i>array</i> in the property, so
/// the names are not added to the existing list but written over it.
/// </para>
/// <para>
/// <b>A reissue takes effect without a restart.</b> The middleware reads through
/// <see cref="IOptionsMonitor{TOptions}"/> and rebuilds its list when a change token fires; this is
/// that token's source, raised by the authority whenever it issues
/// (<see cref="PrinterLeafChangeToken"/>). Without it a reissue that adds a name still answers 400
/// until the next restart.
/// </para>
/// </remarks>
public sealed class PrinterHostFiltering : IPostConfigureOptions<HostFilteringOptions>,
                                           IOptionsChangeTokenSource<HostFilteringOptions>
{
    private readonly PrinterCertificateAuthority _authority;
    private readonly PrinterLeafChangeToken _leafChanged;
    private readonly IOptionsMonitor<PrusaConnectOptions> _connect;

    public PrinterHostFiltering(PrinterCertificateAuthority authority,
                                PrinterLeafChangeToken leafChanged,
                                IOptionsMonitor<PrusaConnectOptions> connect)
    {
        _authority = authority;
        _leafChanged = leafChanged;
        _connect = connect;
    }

    /// <summary>The unnamed options, which is the only instance the middleware reads.</summary>
    public string? Name => Options.DefaultName;

    public IChangeToken GetChangeToken()
    {
        return _leafChanged.GetChangeToken();
    }

    public void PostConfigure(string? name, HostFilteringOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> allowed = [.. options.AllowedHosts];

        foreach (string host in PrinterHosts())
        {
            if (!allowed.Contains(host, StringComparer.OrdinalIgnoreCase))
            {
                allowed.Add(host);
            }
        }

        options.AllowedHosts = allowed;
    }

    /// <summary>
    /// Every name a printer may address this deployment by: the configured host, then every name on
    /// the issued leaf.
    /// </summary>
    /// <remarks>
    /// Read from disk on each call rather than cached, because the options system already caches the
    /// result and asks again only when the token fires — a cache here would be a second copy of the
    /// same answer, one of them stale.
    /// </remarks>
    public IReadOnlyList<string> PrinterHosts()
    {
        List<string> hosts = [];

        PrusaConnectOptions connect = _connect.CurrentValue;

        if (connect.IsPrinterAddressConfigured)
        {
            hosts.Add(connect.PrinterHost.Trim());
        }

        using X509Certificate2? leaf = _authority.LoadLeafIfIssued();

        if (leaf is not null)
        {
            hosts.AddRange(PrinterCertificateAuthority.NamesOf(leaf));
        }

        return [.. hosts.Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
