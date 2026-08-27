using System;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Pages.Printers;

/// <summary>
/// Answers the download from <c>_BundleDownload</c> with the zip itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>A POST, not a link, because there is nothing to link to.</b> The provisioning token is
/// PBKDF2-hashed at rest, so by the time a second request arrives the server cannot produce it again —
/// the page that has it posts it back, and the bundle is assembled around it. That is also why this
/// handler stores nothing and answers nothing on GET.
/// </para>
/// <para>
/// Shared by <see cref="AddModel"/> and <see cref="IndexModel"/>'s reissue rather than duplicated into
/// both: they arrive at the same place — a token in hand and an address to write — and a second copy of
/// this would be a second place for the name check to be forgotten.
/// </para>
/// </remarks>
[Authorize]
public class BundleModel : PageModel
{
    private readonly ProvisioningBundleBuilder _bundles;
    private readonly PrusaConnectOptions _options;
    private readonly ILogger<BundleModel> _logger;

    public BundleModel(ProvisioningBundleBuilder bundles,
                       IOptions<PrusaConnectOptions> options,
                       ILogger<BundleModel> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _bundles = bundles;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Nothing to see. A GET here means a bookmark or a back button, not a working flow.
    /// </summary>
    public IActionResult OnGet()
    {
        return NotFound();
    }

    /// <summary>
    /// Builds and returns the bundle, through whichever endpoint was chosen.
    /// </summary>
    /// <param name="token">The one-time provisioning token, posted back because nothing else holds it.</param>
    /// <param name="hostname">The address to write into the ini.</param>
    /// <param name="printerId">Which printer, for the file name and the log.</param>
    /// <param name="printerName">The printer's name, for the file name and the instructions.</param>
    /// <param name="legacy">Whether to point this printer at the plaintext listener instead.</param>
    /// <param name="legacyConfirmed">The second tick, without which <paramref name="legacy"/> is ignored.</param>
    /// <param name="cancellationToken">The usual.</param>
    /// <remarks>
    /// <b>Both boxes or neither.</b> The confirmation is required here rather than trusted to the
    /// page, so a hand-made POST asking for the plaintext listener has to say the same thing twice as a
    /// browser does. Ignoring a half-made request rather than refusing it is deliberate: the safe
    /// answer is available, and handing somebody a TLS bundle they can still use beats an error page
    /// at the moment they were told the token exists exactly once.
    /// </remarks>
    public async Task<IActionResult> OnPostAsync(string token,
                                                 string hostname,
                                                 int printerId,
                                                 string? printerName,
                                                 bool legacy,
                                                 bool legacyConfirmed,
                                                 CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(hostname))
        {
            return BadRequest();
        }

        PrinterEndpoint? legacyEndpoint = legacy && legacyConfirmed ? PrinterEndpoint.Legacy(_options) : null;

        if (legacy && legacyEndpoint is null)
        {
            // Either the confirmation was missing or this deployment has no such listener. Worth a line
            // in both cases: the first is a form filled in half way, the second is a bundle that
            // would have named a port nothing is listening on.
            _logger.LogInformation("A plaintext provisioning bundle was asked for printer {PrinterId} and not "
                                   + "produced; confirmed: {Confirmed}, listener open: {ListenerOpen}.",
                                   printerId, legacyConfirmed, _options.LegacyPrinterPort is not null);
        }

        PrinterEndpoint endpoint = legacyEndpoint ?? PrinterEndpoint.Default(_options);

        byte[] bundle;

        try
        {
            bundle = await _bundles.BuildAsync(hostname, token, printerName, endpoint, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            // The only way here is a hand-made POST or a certificate reissued between the page being
            // rendered and the button being pressed. Logged rather than swallowed: a bundle refused
            // for a name the certificate does not cover is exactly the failure this check exists to
            // move off the printer's screen and onto ours.
            _logger.LogWarning(ex, "Refused a provisioning bundle for printer {PrinterId}.", printerId);

            return BadRequest(ex.Message);
        }

        if (endpoint.Tls)
        {
            _logger.LogInformation("Provisioning bundle downloaded for printer {PrinterId}, addressed to {Hostname}.",
                                   printerId, hostname);
        }
        else
        {
            // Warning rather than Information, and it names the port: this is the log line somebody
            // reads months later when they are working out why one printer's traffic is readable.
            _logger.LogWarning("PLAINTEXT provisioning bundle downloaded for printer {PrinterId}, addressed to "
                               + "{Hostname}:{Port}. That printer's token, its files and the PrusaLink password it "
                               + "reports will cross the network in clear, and can be altered in flight.",
                               printerId, hostname, endpoint.Port);
        }

        return File(bundle, MediaTypeNames.Application.Zip, FileNameFor(printerName, printerId));
    }

    /// <summary>
    /// A file name that says which printer it belongs to, since a downloads folder will end up holding
    /// several and they are otherwise identical.
    /// </summary>
    private static string FileNameFor(string? printerName, int printerId)
    {
        string slug = new((printerName ?? string.Empty)
                          .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
                          .ToArray());

        slug = string.Join('-', slug.Split('-', StringSplitOptions.RemoveEmptyEntries));

        return string.IsNullOrEmpty(slug) ? $"homespool-printer-{printerId}.zip" : $"homespool-{slug}.zip";
    }
}
