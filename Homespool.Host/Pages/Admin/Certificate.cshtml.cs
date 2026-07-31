using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.Certificates;
using Homespool.Host.PrusaConnect;
using Homespool.Host.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Pages.Admin;

/// <summary>
/// Shows what the printer certificate vouches for, what this machine now looks like, and offers the
/// reissue when those have parted company.
/// </summary>
/// <remarks>
/// <para>
/// <b>The counterpart to a certificate that is deliberately never reissued on its own.</b> Automatic
/// reissue was rejected because it would drop live printer connections whenever an interface appeared
/// and make the certificate a function of what the machine happened to look like at boot. This is
/// what that decision owes the operator in return: somewhere to see the drift and one button to fix
/// it (<c>notes/tls-by-default.md</c>).
/// </para>
/// <para>
/// <b>Reissuing the leaf is a small act, and it is worth knowing why.</b> Printers trust the
/// <i>authority</i>, not this certificate, so a new leaf needs nothing at any printer - no USB visit,
/// no re-provisioning, not even a new bundle unless the address they were told to dial has changed.
/// That asymmetry is the entire reason this deployment mints a CA and a leaf rather than one
/// self-signed certificate.
/// </para>
/// </remarks>
[Authorize(Roles = AdminBootstrap.AdminRole)]
public class CertificateModel : PageModel
{
    private readonly PrinterCertificateAuthority _authority;
    private readonly PrusaConnectOptions _connect;
    private readonly CertificateOptions _certificates;
    private readonly IHostAddressResolver _resolver;
    private readonly ILogger<CertificateModel> _logger;

    public CertificateModel(PrinterCertificateAuthority authority,
                            IOptions<PrusaConnectOptions> connect,
                            IOptions<CertificateOptions> certificates,
                            IHostAddressResolver resolver,
                            ILogger<CertificateModel> logger)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(certificates);

        _authority = authority;
        _connect = connect.Value;
        _certificates = certificates.Value;
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>Whether printers use TLS at all. With it off there is no certificate to show.</summary>
    public bool TlsEnabled => _connect.PrinterTls;

    /// <summary>The address printers are told to dial, or null if none is configured.</summary>
    public string? ConfiguredHost => _connect.IsPrinterAddressConfigured ? _connect.PrinterHost.Trim() : null;

    /// <summary>What the current certificate vouches for. Empty when none has been issued.</summary>
    public IReadOnlyList<string> Covered { get; private set; } = [];

    /// <summary>What a certificate issued now would cover.</summary>
    public IReadOnlyList<string> Current { get; private set; } = [];

    /// <summary>When the leaf expires, or null if there is none.</summary>
    public DateTimeOffset? LeafExpires { get; private set; }

    /// <summary>When the authority expires — the date that costs a USB visit to every printer.</summary>
    public DateTimeOffset? AuthorityExpires { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    /// <summary>
    /// Names from <see cref="Dropping"/> the administrator ticked to carry into the new certificate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Unticked by default, deliberately.</b> Dropping an unreachable name is usually right and is
    /// what this deployment already did, so leaving every box clear reproduces the previous behaviour
    /// exactly — an operator who presses the button without reading gets what they got before, never
    /// something new. Ticking is the deliberate act, taken next to a warning that states the cost.
    /// </para>
    /// <para>
    /// <b>Never trusted as a list of names.</b> It is intersected with what the previous leaf actually
    /// covered before anything is signed, so it can only preserve a name and never introduce one. That
    /// matters more here than almost anywhere: this authority is the entire trust store of every
    /// provisioned printer, with no revocation.
    /// </para>
    /// </remarks>
    [BindProperty]
    public string[] KeepNames { get; set; } = [];

    /// <summary>Names this machine has that the certificate does not, which is what drift looks like.</summary>
    public IReadOnlyList<string> Uncovered =>
        [.. Current.Where(name => !Covered.Contains(name, StringComparer.OrdinalIgnoreCase))];

    /// <summary>
    /// Names the certificate vouches for today that a reissue would <b>not</b> carry over — drift read
    /// in the direction that costs something.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A reissue can narrow the certificate, and that is the one way this button can break a
    /// working printer.</b> Names are filtered at issuance to what a printer could actually reach, so
    /// a name detected once and no longer resolvable is dropped — correctly, since it vouches for
    /// nothing. But a printer provisioned with that name in its ini is still dialling it, and after
    /// the reissue and the proxy reload its handshake fails on a leaf that no longer covers the name
    /// it asked for. mbedTLS reports that as a bare TLS error naming neither the name nor the
    /// certificate, and the fix is a USB visit to repoint that printer.
    /// </para>
    /// <para>
    /// So this is surfaced before the button rather than discovered afterwards. It is deliberately not
    /// a block: narrowing is usually right, and the operator is the only one who knows which names
    /// their printers were given. <c>PrusaConnect:PrinterHost</c> is taken as given at issuance and so
    /// never appears here.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Dropping =>
        [.. Covered.Where(name => !Current.Contains(name, StringComparer.OrdinalIgnoreCase))];

    /// <summary>
    /// True when the address printers are actually told to dial is absent from the certificate — the
    /// drift that stops provisioning outright rather than merely one address working.
    /// </summary>
    public bool ConfiguredHostUncovered =>
        ConfiguredHost is not null && !Covered.Contains(ConfiguredHost, StringComparer.OrdinalIgnoreCase);

    public Task OnGetAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostReissueAsync(CancellationToken cancellationToken)
    {
        if (!TlsEnabled)
        {
            // Nothing serves a certificate in this configuration, so issuing one would leave a file
            // on disk that no listener reads and no printer sees.
            StatusMessage = "Printers connect over plain HTTP in this deployment, so there is no certificate to reissue.";

            return RedirectToPage();
        }

        IReadOnlyList<string> detected = await PrinterCertificateNames.ForThisMachineAsync(
            _connect, _certificates.ParsedContainerNetworks, _resolver, cancellationToken);

        // Read from the leaf on disk rather than from the Dropping property, which is only populated
        // by LoadAsync on the GET. Taken before IssueLeaf overwrites it, because a narrowing is
        // invisible afterwards: the old certificate is gone and the only evidence left is a printer
        // that stopped connecting.
        IReadOnlyList<string> previouslyCovered = [];

        using (X509Certificate2? existing = _authority.LoadLeafIfIssued())
        {
            if (existing is not null)
            {
                previouslyCovered = PrinterCertificateAuthority.NamesOf(existing);
            }
        }

        // WHAT THE OPERATOR ASKED TO CARRY OVER, INTERSECTED WITH WHAT THE OLD LEAF ACTUALLY COVERED.
        // The intersection is the security control, not a tidiness one: this is a request body, and
        // without it a crafted POST could put any name at all into a certificate that every provisioned
        // printer trusts absolutely. Restricting to names the previous leaf already vouched for means
        // this can only ever preserve a name, never introduce one.
        string[] kept =
        [
            .. previouslyCovered.Where(name => KeepNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                                .Where(name => !detected.Contains(name, StringComparer.OrdinalIgnoreCase)),
        ];

        // Detected first: PrinterCertificateAuthority takes the first name as the subject, and the
        // configured host leads the detected list, so appending keeps that.
        string[] names = [.. detected, .. kept];

        if (names.Length == 0)
        {
            StatusMessage = "No usable address could be detected and PrusaConnect:PrinterHost is not set, so a new "
                          + "certificate would cover nothing a printer could verify. Set the address first.";

            return RedirectToPage();
        }

        string[] dropped = [.. previouslyCovered.Where(name => !names.Contains(name, StringComparer.OrdinalIgnoreCase))];

        using X509Certificate2 issued = _authority.IssueLeaf(names);

        if (kept.Length > 0)
        {
            _logger.LogInformation("The reissued printer certificate keeps {Kept} at the administrator's request, "
                                   + "although this machine no longer answers on those names. Printers already dialling "
                                   + "them keep working; nothing else does.", string.Join(", ", kept));
        }

        if (dropped.Length > 0)
        {
            _logger.LogWarning("The reissued printer certificate NO LONGER covers {Dropped}, which the previous one "
                               + "did. Any printer whose ini tells it to dial one of those names will fail its "
                               + "handshake once the proxy is reloaded, reporting a bare TLS error, and needs a USB "
                               + "visit to repoint it. They were dropped because this machine no longer answers on "
                               + "them and were not ticked to keep.", string.Join(", ", dropped));
        }

        _logger.LogWarning("The printer certificate was reissued for {Names} by {User}. It is served once the proxy "
                           + "reloads; until then the previous certificate is still on the wire.",
                           string.Join(", ", names), User.Identity?.Name);

        // The reload is the honest part of the message, and it is the proxy's rather than this
        // process's: nginx read the old certificate when it started and holds it until told
        // otherwise, so a page that said "done" would be describing a file rather than what printers
        // actually meet. It used to say RESTART THE SERVER, which was true when Kestrel served the
        // certificate and is now both wrong and needlessly expensive - reloading nginx keeps the
        // application, its database and every user session up.
        //
        // The "next time each printer reconnects" clause is not hedging. Verified on hardware
        // 2026-07-31: `nginx -s reload` is graceful, so a printer's existing WebSocket stays on the
        // old worker with the OLD certificate until that connection closes - and a printer connection
        // is idle-but-open for hours, so the old worker generation lives exactly as long. It is
        // harmless, because both leaves chain to the authority the printer trusts, but an operator
        // who reissues to fix an address, reloads, and then checks what is being served would
        // otherwise conclude the reload had not worked.
        //
        // Plain text, no markup: this is rendered as-is, and a page that shows an operator literal
        // asterisks around its most important sentence has undermined the sentence.
        StatusMessage = $"Reissued for {string.Join(", ", names)}, valid until "
                      + $"{issued.NotAfter.ToUniversalTime():yyyy-MM-dd}. RELOAD THE PROXY to serve it - run "
                      + "\"docker compose exec proxy nginx -s reload\". New connections get it immediately; a printer "
                      + "that is already connected keeps meeting the previous certificate until it next reconnects, "
                      + "which is harmless because both are signed by the same authority. Nothing is needed at the "
                      + "printers themselves: that authority has not changed.";

        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Current = await PrinterCertificateNames.ForThisMachineAsync(
            _connect, _certificates.ParsedContainerNetworks, _resolver, cancellationToken);

        if (!TlsEnabled)
        {
            return;
        }

        using X509Certificate2? leaf = _authority.LoadLeafIfIssued();

        if (leaf is not null)
        {
            Covered = PrinterCertificateAuthority.NamesOf(leaf);
            LeafExpires = leaf.NotAfter.ToUniversalTime();
        }

        using X509Certificate2 authority = _authority.EnsureAuthority();
        AuthorityExpires = authority.NotAfter.ToUniversalTime();
    }
}
