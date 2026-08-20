using System;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<BundleModel> _logger;

    public BundleModel(ProvisioningBundleBuilder bundles, ILogger<BundleModel> logger)
    {
        _bundles = bundles;
        _logger = logger;
    }

    /// <summary>
    /// Nothing to see. A GET here means a bookmark or a back button, not a working flow.
    /// </summary>
    public IActionResult OnGet()
    {
        return NotFound();
    }

    public async Task<IActionResult> OnPostAsync(string token,
                                                 string hostname,
                                                 int printerId,
                                                 string? printerName,
                                                 CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(hostname))
        {
            return BadRequest();
        }

        byte[] bundle;

        try
        {
            bundle = await _bundles.BuildAsync(hostname, token, printerName, cancellationToken);
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

        _logger.LogInformation("Provisioning bundle downloaded for printer {PrinterId}, addressed to {Hostname}.",
                               printerId, hostname);

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
