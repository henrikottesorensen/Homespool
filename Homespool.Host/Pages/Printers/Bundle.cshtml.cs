using System;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Host.PrusaConnect;
using Homespool.Model;
using Homespool.Model.Entities;

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
/// <para>
/// <b><c>[Authorize]</c> is not the whole check</b>, and could not be: the printer this bundle is for
/// arrives in the POST body, so the per-printer half is a
/// <see cref="PrinterAccessService"/> call in <see cref="OnPostAsync"/> - see
/// <c>Authorisation/</c> for why a resource-shaped rule cannot be an attribute.
/// </para>
/// </remarks>
[Authorize]
public class BundleModel : PageModel
{
    private readonly ProvisioningBundleBuilder _bundles;
    private readonly PrinterAccessService _access;
    private readonly UserManager<HSUser> _userManager;
    private readonly PrusaConnectOptions _options;
    private readonly ILogger<BundleModel> _logger;

    public BundleModel(ProvisioningBundleBuilder bundles,
                       PrinterAccessService access,
                       UserManager<HSUser> userManager,
                       IOptionsSnapshot<PrusaConnectOptions> options,
                       ILogger<BundleModel> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _bundles = bundles;
        _access = access;
        _userManager = userManager;
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
    /// <param name="printerId">
    /// Which printer. Checked against the caller, and then the only thing this handler knows about
    /// it — the name in the file and in the instructions is read from the row, not posted.
    /// </param>
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
                                                 bool legacy,
                                                 bool legacyConfirmed,
                                                 CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(hostname))
        {
            return BadRequest();
        }

        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            // [Authorize] should make this unreachable; fail closed rather than build for an
            // invented id.
            return Forbid();
        }

        // The bundle is assembled around a token the caller posted, so this is not the gate that
        // keeps that credential from a stranger - RegenerateProvisioningTokenAsync is, and it asks
        // for the same capability. What this protects is the plaintext warning below, which is read
        // months later as the record of whose traffic went in clear: an unchecked id lets any
        // account write that line about somebody else's machine. The row it returns then names the
        // printer in the file and in the instructions, so nothing posted here decides what the
        // bundle says about the machine it is for.
        //
        // Both refusals become one answer below, where the service tells them apart: this id
        // arrives on a hand-made POST rather than from something that already resolved it, so
        // telling them apart would enumerate other people's printers for the price of a POST.
        Printer printer;

        try
        {
            printer = await _access.RequireAsync(printerId,
                                                 CallerResolver.For(user, User),
                                                 Capability.ManagePrinter,
                                                 cancellationToken);
        }
        catch (PrinterNotFoundException)
        {
            return NotFound();
        }
        catch (TeamAccessDeniedException)
        {
            return NotFound();
        }

        PrinterEndpoint? legacyEndpoint = legacy && legacyConfirmed ? PrinterEndpoint.Legacy(_options) : null;

        if (legacy && legacyEndpoint is null)
        {
            // Either the confirmation was missing or this deployment has no such listener. Worth a line
            // in both cases: the first is a form filled in half way, the second is a bundle that
            // would have named a port nothing is listening on.
            _logger.LogInformation("A plaintext provisioning bundle was asked for printer {PrinterUuid} and not "
                                   + "produced; confirmed: {Confirmed}, listener open: {ListenerOpen}.",
                                   printer.Uuid, legacyConfirmed, _options.LegacyPrinterPort is not null);
        }

        PrinterEndpoint endpoint = legacyEndpoint ?? PrinterEndpoint.Default(_options);

        // The name as the builder will have it, so the lines below report the address that was
        // actually written rather than the spelling it arrived in - and so that only a string the
        // builder accepted is ever logged. That is what keeps a newline out of the warning.
        string name = hostname.Trim();

        byte[] bundle;

        try
        {
            bundle = await _bundles.BuildAsync(name, token, printer.Name, endpoint, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            // The only way here is a hand-made POST or a certificate reissued between the page being
            // rendered and the button being pressed. Logged rather than swallowed: a bundle refused
            // for a name the certificate does not cover is exactly the failure this check exists to
            // move off the printer's screen and onto ours.
            _logger.LogWarning(ex, "Refused a provisioning bundle for printer {PrinterUuid}.", printer.Uuid);

            return BadRequest(ex.Message);
        }

        if (endpoint.Tls)
        {
            _logger.LogInformation("Provisioning bundle downloaded for printer {PrinterUuid}, addressed to {Hostname}.",
                                   printer.Uuid, name);
        }
        else
        {
            // Warning rather than Information, and it names the port: this is the log line somebody
            // reads months later when they are working out why one printer's traffic is readable.
            // The uuid rather than the row id for the same reason: whoever reads it is following one
            // printer across the enrolment lines PrusaConnectService writes either side of this one.
            _logger.LogWarning("PLAINTEXT provisioning bundle downloaded for printer {PrinterUuid}, addressed to "
                               + "{Hostname}:{Port}. That printer's token, its files, and the WiFi SSID and "
                               + "PrusaLink password it reports will cross the network in clear, and can be altered "
                               + "in flight.",
                               printer.Uuid, name, endpoint.Port);
        }

        return File(bundle, MediaTypeNames.Application.Zip, FileNameFor(printer.Name, printerId));
    }

    /// <summary>
    /// A file name that says which printer it belongs to, since a downloads folder will end up holding
    /// several and they are otherwise identical.
    /// </summary>
    /// <remarks>
    /// <b>The row id in the fallback, where the log lines carry the uuid.</b> They are read by
    /// different people: a uuid in a downloads folder is unreadable, and a name is what tells two of
    /// these apart on a stick.
    /// </remarks>
    private static string FileNameFor(string? printerName, int printerId)
    {
        string slug = new((printerName ?? string.Empty)
                          .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
                          .ToArray());

        slug = string.Join('-', slug.Split('-', StringSplitOptions.RemoveEmptyEntries));

        return string.IsNullOrEmpty(slug) ? $"homespool-printer-{printerId}.zip" : $"homespool-{slug}.zip";
    }
}
