using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;

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
    private readonly ILogger<CertificateModel> _logger;

    public CertificateModel(PrinterCertificateAuthority authority,
                            IOptions<PrusaConnectOptions> connect,
                            ILogger<CertificateModel> logger)
    {
        ArgumentNullException.ThrowIfNull(connect);

        _authority = authority;
        _connect = connect.Value;
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

    /// <summary>Names this machine has that the certificate does not, which is what drift looks like.</summary>
    public IReadOnlyList<string> Uncovered =>
        [.. Current.Where(name => !Covered.Contains(name, StringComparer.OrdinalIgnoreCase))];

    /// <summary>
    /// True when the address printers are actually told to dial is absent from the certificate — the
    /// drift that stops provisioning outright rather than merely one address working.
    /// </summary>
    public bool ConfiguredHostUncovered =>
        ConfiguredHost is not null && !Covered.Contains(ConfiguredHost, StringComparer.OrdinalIgnoreCase);

    public void OnGet() => Load();

    public IActionResult OnPostReissue()
    {
        if (!TlsEnabled)
        {
            // Nothing serves a certificate in this configuration, so issuing one would leave a file
            // on disk that no listener reads and no printer sees.
            StatusMessage = "Printers connect over plain HTTP in this deployment, so there is no certificate to reissue.";

            return RedirectToPage();
        }

        IReadOnlyList<string> names = PrinterCertificateNames.ForThisMachine(_connect);

        if (names.Count == 0)
        {
            StatusMessage = "No usable address could be detected and PrusaConnect:PrinterHost is not set, so a new "
                          + "certificate would cover nothing a printer could verify. Set the address first.";

            return RedirectToPage();
        }

        using X509Certificate2 issued = _authority.IssueLeaf(names);

        _logger.LogWarning("The printer certificate was reissued for {Names} by {User}. It is served from the next "
                           + "restart; until then the previous certificate is still on the wire.",
                           string.Join(", ", names), User.Identity?.Name);

        // The restart is the honest part of the message. Kestrel bound the old certificate when it
        // started and holds it for the life of the process, so a page that said "done" would be
        // describing a file rather than what printers actually meet.
        // Plain text, no markup: this is rendered as-is, and a page that shows an operator literal
        // asterisks around its most important sentence has undermined the sentence.
        StatusMessage = $"Reissued for {string.Join(", ", names)}, valid until "
                      + $"{issued.NotAfter.ToUniversalTime():yyyy-MM-dd}. RESTART THE SERVER to serve it - until then "
                      + "printers still meet the previous certificate. Nothing is needed at the printers themselves: "
                      + "they trust the authority, which has not changed.";

        return RedirectToPage();
    }

    private void Load()
    {
        Current = PrinterCertificateNames.ForThisMachine(_connect);

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
