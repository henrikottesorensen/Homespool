using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;

using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
using Homespool.Host.Notifications;
using Homespool.Host.Notifications.WebPush;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Account.Manage;

/// <summary>
/// The browsers that receive this account's notifications: enabling this one, sending any of them a
/// test, and removing them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Subscribing is a script's work and the storing is an ordinary form post.</b> The browser has to
/// be asked for permission and for a subscription, which only script can do; what it hands back is
/// written into this page's form and submitted natively, so the antiforgery token and the
/// post/redirect/get travel the way every other page's do.
/// </para>
/// <para>
/// <b>No endpoint is ever rendered.</b> It is a capability URL. The script recognises its own browser's
/// row by a hash of the endpoint instead - enough to match, and nothing anyone could send to.
/// </para>
/// <para>
/// <b>Adding a browser takes a recent proof; testing and removing one do not.</b> A subscription
/// outlives the session that made it - signing out leaves it in place - so a session somebody else got
/// hold of could otherwise add their own browser and go on hearing about this account's printers after
/// the session was ended. That is the reasoning that gates minting an API token, and it applies here in
/// the same direction: removing is what the holder of a stolen session would least want to do.
/// </para>
/// </remarks>
[Authorize]
public class NotificationsModel : PageModel
{
    /// <summary>How many base64url characters of the endpoint's SHA-256 a row carries for matching.</summary>
    public const int EndpointHashLength = 22;

    private readonly NotificationDestinationService _destinations;
    private readonly Services.PrinterQueryService _printers;
    private readonly VapidKeyStore _keys;
    private readonly UserManager<HSUser> _userManager;
    private readonly RecentProof _proof;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public NotificationsModel(NotificationDestinationService destinations,
                              Services.PrinterQueryService printers,
                              VapidKeyStore keys,
                              UserManager<HSUser> userManager,
                              RecentProof proof,
                              IStringLocalizer<SharedResource> localiser)
    {
        _destinations = destinations;
        _printers = printers;
        _keys = keys;
        _userManager = userManager;
        _proof = proof;
        _localiser = localiser;
    }

    /// <summary>Whether the person has proved themselves recently, so the enable button is worth offering.</summary>
    public bool Proved { get; private set; }

    /// <summary>This account's destinations, oldest first.</summary>
    public IReadOnlyList<DestinationRow> Destinations { get; private set; } = [];

    /// <summary>Each kind of notification, and whether this account hears it.</summary>
    public IReadOnlyList<KindChoice> Kinds { get; private set; } = [];

    /// <summary>Each printer this account may see, and whether it hears about it.</summary>
    public IReadOnlyList<PrinterChoice> Printers { get; private set; } = [];

    /// <summary>The deployment's public VAPID key, which a browser subscribes with.</summary>
    public string ApplicationServerKey { get; private set; } = string.Empty;

    [TempData]
    public string? StatusMessage { get; set; }

    /// <summary>A sentence about something that did not work, shown apart from the success messages.</summary>
    [TempData]
    public string? ProblemMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return NotFound();
        }

        ApplicationServerKey = (await _keys.GetAsync(cancellationToken)).PublicKey;
        Proved = _proof.IsProved(HttpContext, user.Id);

        IReadOnlyList<NotificationDestination> destinations = await _destinations.ListAsync(user.Id, cancellationToken);

        Destinations = [.. destinations.Select(DestinationRow.From)];

        IReadOnlySet<NotificationKind> muted = await _destinations.MutedAsync(user.Id, cancellationToken);
        Kinds = [.. NotificationMutes.Choosable.Select(kind => new KindChoice(kind, LabelKey(kind), !muted.Contains(kind)))];

        IReadOnlySet<Guid> mutedPrinters = await _destinations.MutedPrintersAsync(user.Id, cancellationToken);
        IReadOnlyList<Printer> visible = await _printers.ListPrintersForUserAsync(Caller.Unscoped(user.Id), cancellationToken);
        Printers = [.. visible.Select(printer => new PrinterChoice(printer.Uuid, PrinterDisplayName.For(printer), !mutedPrinters.Contains(printer.Uuid)))
                              .OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase)];

        return Page();
    }

    /// <summary>
    /// Saves which kinds this account hears: every kind offered and not ticked is turned off.
    /// </summary>
    /// <remarks>
    /// No recent proof, unlike adding a browser: this only narrows what the account already hears, and
    /// turning everything off is the one thing the holder of a stolen session gains nothing from.
    /// </remarks>
    /// <param name="enabled">The kinds ticked.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    public async Task<IActionResult> OnPostKindsAsync(List<NotificationKind> enabled, CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return NotFound();
        }

        await _destinations.SetMutedAsync(user.Id,
                                          NotificationMutes.Choosable.Where(kind => !enabled.Contains(kind)),
                                          cancellationToken);

        StatusMessage = _localiser["Notifications_KindsSaved"];

        return RedirectToPage();
    }

    /// <summary>
    /// Saves which printers this account hears about: every printer it may see and did not tick is
    /// muted, whatever kind of notification it would have sent.
    /// </summary>
    /// <remarks>
    /// Rewritten whole from the printers the account may see now, so a printer it has since lost access
    /// to drops out of the list rather than staying muted for nobody.
    /// </remarks>
    /// <param name="enabled">The printers ticked.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    public async Task<IActionResult> OnPostPrintersAsync(List<Guid> enabled, CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return NotFound();
        }

        IReadOnlyList<Printer> visible = await _printers.ListPrintersForUserAsync(Caller.Unscoped(user.Id), cancellationToken);

        await _destinations.SetMutedPrintersAsync(user.Id,
                                                  visible.Select(printer => printer.Uuid).Where(uuid => !enabled.Contains(uuid)),
                                                  cancellationToken);

        StatusMessage = _localiser["Notifications_PrintersSaved"];

        return RedirectToPage();
    }

    /// <summary>The resource key naming a kind on this page. Written out, so every key is findable.</summary>
    public static string LabelKey(NotificationKind kind)
    {
        return kind switch
        {
            NotificationKind.PrinterNeedsAttention => "Notifications_KindPrinterNeedsAttention",
            NotificationKind.FilamentChangeSoon => "Notifications_KindFilamentChangeSoon",
            NotificationKind.PrinterLost => "Notifications_KindPrinterLost",
            NotificationKind.PrintFinished => "Notifications_KindPrintFinished",
            NotificationKind.PrintDidNotFinish => "Notifications_KindPrintDidNotFinish",
            NotificationKind.QueueHeld => "Notifications_KindQueueHeld",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a kind a person can choose."),
        };
    }

    /// <summary>Stores the subscription the script got from this browser.</summary>
    /// <param name="endpoint">The push service's address for this browser.</param>
    /// <param name="p256dh">The browser's public key, base64url.</param>
    /// <param name="auth">The browser's authentication secret, base64url.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    [RequireRecentProof]
    public async Task<IActionResult> OnPostSubscribeAsync(string? endpoint,
                                                          string? p256dh,
                                                          string? auth,
                                                          CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return NotFound();
        }

        string name = BrowserNames.FromUserAgent(Request.Headers.UserAgent.ToString(),
                                                 _localiser["Notifications_UnknownBrowser"].Value);

        WebPushSubscribeResult result =
            await _destinations.SubscribeWebPushAsync(user.Id, endpoint, p256dh, auth, name, cancellationToken);

        switch (result)
        {
            case WebPushSubscribeResult.Subscribed:
                StatusMessage = _localiser["Notifications_Enabled"];
                break;

            case WebPushSubscribeResult.EndpointNotAllowed:
                ProblemMessage = _localiser["Notifications_EndpointRefused"];
                break;

            case WebPushSubscribeResult.KeysInvalid:
                ProblemMessage = _localiser["Notifications_SubscriptionInvalid"];
                break;

            case WebPushSubscribeResult.TooMany:
                ProblemMessage = _localiser["Notifications_TooManyBrowsers", NotificationDestinationService.MaxPerAccount];
                break;

            default:
                throw new InvalidOperationException($"Subscribing answered {result}.");
        }

        return RedirectToPage();
    }

    /// <summary>Sends one of this account's browsers a test notification.</summary>
    /// <param name="uuid">The destination's public id.</param>
    /// <param name="cancellationToken">Cancels the delivery.</param>
    public async Task<IActionResult> OnPostTestAsync(Guid uuid, CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return NotFound();
        }

        TestSendResult outcome = await _destinations.SendTestAsync(user.Id, uuid, cancellationToken);

        switch (outcome)
        {
            case TestSendResult.Delivered:
                StatusMessage = _localiser["Notifications_TestSent"];
                break;

            case TestSendResult.Gone:
                ProblemMessage = _localiser["Notifications_TestGone"];
                break;

            case TestSendResult.Transient:
                ProblemMessage = _localiser["Notifications_TestTransient"];
                break;

            case TestSendResult.Refused:
                ProblemMessage = _localiser["Notifications_TestRefused"];
                break;

            case TestSendResult.NotFound:
                ProblemMessage = _localiser["Notifications_NoSuchBrowser"];
                break;

            case TestSendResult.TooSoon:
                ProblemMessage = _localiser["Notifications_TestTooSoon"];
                break;

            default:
                throw new InvalidOperationException($"A test delivery answered {outcome}.");
        }

        return RedirectToPage();
    }

    /// <summary>Removes one of this account's browsers.</summary>
    /// <param name="uuid">The destination's public id.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    public async Task<IActionResult> OnPostRemoveAsync(Guid uuid, CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return NotFound();
        }

        if (await _destinations.RemoveAsync(user.Id, uuid, cancellationToken))
        {
            StatusMessage = _localiser["Notifications_Removed"];
        }
        else
        {
            ProblemMessage = _localiser["Notifications_NoSuchBrowser"];
        }

        return RedirectToPage();
    }

    /// <summary>
    /// The start of an endpoint's SHA-256, base64url: what a row carries so the script can tell its own
    /// browser's row without the page holding the endpoint.
    /// </summary>
    public static string EndpointHash(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)))[..EndpointHashLength];
    }

    /// <summary>One kind of notification as the page offers it.</summary>
    /// <param name="Kind">The kind.</param>
    /// <param name="LabelKey">The resource key naming it.</param>
    /// <param name="Enabled">Whether this account hears it.</param>
    public sealed record KindChoice(NotificationKind Kind, string LabelKey, bool Enabled);

    /// <summary>One printer as the page offers it.</summary>
    /// <param name="Uuid">The printer's public id, which the form posts.</param>
    /// <param name="Name">What the printer is called.</param>
    /// <param name="Enabled">Whether this account hears about it.</param>
    public sealed record PrinterChoice(Guid Uuid, string Name, bool Enabled);

    /// <summary>One destination as the list shows it.</summary>
    /// <param name="Uuid">What the buttons carry.</param>
    /// <param name="Name">What the browser is called.</param>
    /// <param name="CreatedAt">When it was enabled.</param>
    /// <param name="LastDeliveredAt">When something last reached it.</param>
    /// <param name="ConsecutiveFailures">How many deliveries in a row have not.</param>
    /// <param name="EndpointHash">For the script to recognise this browser's own row; null for other kinds.</param>
    public sealed record DestinationRow(Guid Uuid,
                                        string Name,
                                        DateTimeOffset CreatedAt,
                                        DateTimeOffset? LastDeliveredAt,
                                        int ConsecutiveFailures,
                                        string? EndpointHash)
    {
        /// <summary>The row for <paramref name="destination"/>.</summary>
        public static DestinationRow From(NotificationDestination destination)
        {
            ArgumentNullException.ThrowIfNull(destination);

            return new DestinationRow(destination.Uuid,
                                      destination.Name,
                                      destination.CreatedAt,
                                      destination.LastDeliveredAt,
                                      destination.ConsecutiveFailures,
                                      destination is WebPushDestination browser ? NotificationsModel.EndpointHash(browser.Endpoint) : null);
        }
    }
}
