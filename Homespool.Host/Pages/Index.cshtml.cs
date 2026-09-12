using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

using Homespool.Host.Authorisation;
using Homespool.Host.Localisation;
using Homespool.Host.PrintFiles;
using Homespool.Host.Printing;
using Homespool.Host.Queue;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages;

/// <summary>
/// The front page: the printers you actually use, biggest thing on the screen.
/// </summary>
/// <remarks>
/// <para>
/// <b>Anonymous, and that shapes the whole page.</b> There is no <c>[Authorize]</c> here and there
/// should not be - a signed-out visitor gets the holding page, which is the mark and a tagline. The
/// shortcuts appear only once we know whose they are, so <see cref="Shortcuts"/> being empty is a
/// normal state rather than a failure and the view says something different for each reason it can
/// be empty.
/// </para>
/// <para>
/// <b>The tiles poll, like the printer page's status card.</b> A front page whose plaques go stale
/// is the exact complaint that started the printer-page rebuild - a page left open showed a live
/// photograph beside a dead status - and shipping a fresh one with
/// the same defect would be perverse when the machinery already exists.
/// </para>
/// </remarks>
[AllowAnonymous]
[BoundedUpload] // MaxUploadBytes, applied before the body is read - see BoundedUploadAttribute.
public class IndexModel : PageModel
{
    /// <summary>
    /// How many tiles the page shows.
    /// </summary>
    /// <remarks>
    /// <b>Six, because the tile is large by design.</b> The ask was a big drawing and a small plaque;
    /// past six the grid either wraps into a second row of scrolling or shrinks the drawing back to
    /// the icon it was meant not to be. The printers listing is one click away and shows everything.
    /// </remarks>
    private const int TileCount = 6;

    /// <summary>
    /// How far back "often used" looks.
    /// </summary>
    /// <remarks>
    /// <b>A window rather than all time, so the ordering keeps up with you.</b> Counting for ever
    /// means a printer hammered during one project outranks the one used every week since, and the
    /// front page slowly becomes a museum of what you used to do. Ninety days is long enough that an
    /// ordinary fortnight away does not empty it.
    /// </remarks>
    private static readonly TimeSpan UsageWindow = TimeSpan.FromDays(90);

    private readonly PrinterQueryService _printers;
    private readonly PrintHistoryService _history;
    private readonly PrintQueueService _queue;
    private readonly PrinterAccessService _access;
    private readonly TileDrop _drop;
    private readonly PrinterConnectionRegistry _connections;
    private readonly PrinterStatusText _statusText;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly UserManager<HSUser> _userManager;
    private readonly TimeProvider _clock;

    public IndexModel(PrinterQueryService printers,
                      PrintHistoryService history,
                      PrintQueueService queue,
                      PrinterAccessService access,
                      TileDrop drop,
                      PrinterConnectionRegistry connections,
                      PrinterStatusText statusText,
                      IStringLocalizer<SharedResource> localiser,
                      UserManager<HSUser> userManager,
                      TimeProvider clock)
    {
        _printers = printers;
        _history = history;
        _queue = queue;
        _access = access;
        _drop = drop;
        _connections = connections;
        _statusText = statusText;
        _localiser = localiser;
        _userManager = userManager;
        _clock = clock;
    }

    /// <summary>The tiles, most-used first. Empty for a signed-out visitor.</summary>
    public IReadOnlyList<PrinterShortcut> Shortcuts { get; private set; } = [];

    /// <summary>Whether we know who is reading, which decides which page this is.</summary>
    public bool SignedIn { get; private set; }

    /// <summary>
    /// Whether the reader can see any printer at all - so the view can tell "none yet" from "none
    /// you have used", which want different words and a different link.
    /// </summary>
    public bool HasAnyPrinter { get; private set; }

    /// <summary>Whether a drop has anywhere to put its bytes. False makes the tiles inert targets.</summary>
    public bool CanUpload { get; private set; }

    /// <summary>What a drop did, said per file. Rendered once and then gone.</summary>
    [TempData]
    public string? StatusMessage { get; set; }

    /// <summary>Whether that message is good news, which decides the alert's colour.</summary>
    [TempData]
    public bool StatusSuccess { get; set; }

    /// <summary>What a printer's status says, in the reader's language.</summary>
    public string StatusText(PrinterStatus? status)
    {
        return _statusText.For(status);
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    /// <summary>
    /// The tiles on their own, for the poll.
    /// </summary>
    /// <remarks>
    /// <b>It reloads everything the fragment renders</b>, including the usage counts that decide the
    /// order. That is the rule - a polled fragment may only render state its own handler loads -
    /// and here it also means a print started from
    /// another tab reorders the page by itself.
    /// </remarks>
    public async Task<IActionResult> OnGetTilesAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);

        return Partial("_PrinterShortcuts", this);
    }

    /// <summary>
    /// Answers what a drop would collide with, before it uploads anything - the browser sends names
    /// and gets back the dialog as markup. The work is <see cref="TileDrop"/>'s; this decides who may
    /// ask, and where the camera's still is served from.
    /// </summary>
    public async Task<IActionResult> OnPostConflictsAsync(Guid uuid,
                                                          string[] names,
                                                          CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return Forbid();
        }

        Caller caller = CallerResolver.For(user, User);

        PrinterWithState? row = await _printers.GetPrinterWithStateForUserAsync(uuid, caller, cancellationToken);

        if (row is null)
        {
            return NotFound();
        }

        if (!caller.Allows(Capability.UploadOwnFiles))
        {
            return Forbid();
        }

        Guid? camera = await _drop.FirstCameraAsync(row.Printer.Id, caller, cancellationToken);
        string? frame = camera is { } cameraUuid ? Url.Action("Frame", "Camera", new { uuid = cameraUuid }) : null;

        return Partial("_TileDrop", await _drop.PromptAsync(row, caller, names, frame, cancellationToken));
    }

    /// <summary>
    /// Carries out a drop: upload each file, then queue, then optionally make the printer ready.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The upload is bounded by <see cref="BoundedUploadAttribute"/> on this class</b>, at the
    /// configured cap plus form overhead and before a byte is read - the same bound the Files page
    /// carries, and what makes the cap this page advertises the cap it enforces. <b>Because the
    /// parameter is a list, both halves of that bound are load-bearing here</b>: the multipart limit
    /// applies to each file, and the server's ceiling to the sum of them, so several files spend one
    /// cap between them and no single one may exceed it. A drag sends one, so the sum is a limit on
    /// what a hand-built post can do rather than something the dialog can walk into.
    /// </para>
    /// <para>
    /// <b>What is deliberately not here is the Files page's <c>file.Length</c> check</b>, which turns
    /// the narrow band between the cap and the cap plus overhead into a localised message. A drop
    /// refused for size arrives as a bare 4xx instead - which is the trade that attribute documents,
    /// and the reason the dialog states the cap up front.
    /// </para>
    /// <para>
    /// <b>Ready-and-print is refused here for a printer that does not permit remote readying</b>,
    /// whatever the browser sent: the dialog hides the button, and a button nobody rendered is still
    /// a request somebody can make. The rest of the rules are <see cref="TileDrop"/>'s.
    /// </para>
    /// </remarks>
    public async Task<IActionResult> OnPostDropAsync(Guid uuid,
                                                     string action,
                                                     List<IFormFile> files,
                                                     string[] replace,
                                                     CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return Forbid();
        }

        Caller caller = CallerResolver.For(user, User);

        PrinterWithState? row = await _printers.GetPrinterWithStateForUserAsync(uuid, caller, cancellationToken);

        if (row is null)
        {
            return NotFound();
        }

        if (TileDrop.Readies(action) && !row.Printer.RemoteReadyAllowed)
        {
            return Forbid();
        }

        (StatusMessage, StatusSuccess) =
            await _drop.DropAsync(row, caller, action, files, replace, User.Identity?.Name, cancellationToken);

        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            SignedIn = false;
            Shortcuts = [];

            return;
        }

        SignedIn = true;

        Caller caller = CallerResolver.For(user, User);

        IReadOnlyList<PrinterWithState> visible =
            await _printers.ListPrintersWithStateForUserAsync(caller, cancellationToken);

        HasAnyPrinter = visible.Count > 0;

        if (!HasAnyPrinter)
        {
            Shortcuts = [];

            return;
        }

        List<int> ids = visible.Select(row => row.Printer.Id).ToList();

        IReadOnlyDictionary<int, PrinterUsage> usage = await _history.CountForUserAsync(
            caller.UserId,
            ids,
            _clock.GetUtcNow() - UsageWindow,
            cancellationToken);

        // One grouped count for every tile, rather than a queue read per printer. Six printers would
        // otherwise be six round trips on a page that refreshes itself every ten seconds.
        IReadOnlyDictionary<int, int> queued = await _queue.CountByPrinterAsync(ids, cancellationToken);

        // Printers you have never used stay in the list rather than being filtered out: a rack of
        // three where you have only ever used two should still show the third, or the front page
        // would hide a printer from the person most likely to be looking for it. They sort last, on
        // a zero count and a floor timestamp, which is exactly where "never used" belongs.
        var ranked = visible
                     .Select(row => new
                     {
                         Row = row,
                         Usage = usage.TryGetValue(row.Printer.Id, out PrinterUsage? used) ?
                             used :
                             new PrinterUsage(0, DateTimeOffset.MinValue),
                     })
                     .OrderByDescending(entry => entry.Usage.Jobs)
                     .ThenByDescending(entry => entry.Usage.LastStartedAt)
                     .ThenBy(entry => entry.Row.Printer.Id)
                     .Take(TileCount)
                     .ToList();

        // Asked per printer rather than once for the page, because it is a per-printer answer: a rack
        // can mix printers you may print on with printers you may only watch. Bounded by TileCount, so
        // it is at most six questions however many printers a person can see.
        List<PrinterShortcut> shortcuts = [];

        foreach (var entry in ranked)
        {
            bool canPrint = await _access.AllowsAsync(
                entry.Row.Printer.Id, caller, Capability.Print, cancellationToken);

            shortcuts.Add(ShortcutFor(entry.Row, entry.Usage.Jobs, queued, canPrint));
        }

        Shortcuts = shortcuts;

        // Asked of the caller, not of a printer. Uploading writes into the reader's own tree and no
        // printer is party to it, which is why PrintFileCatalog checks Caller.Allows directly rather
        // than going through PrinterAccessService. Without it a drop has nowhere to put the bytes.
        CanUpload = caller.Allows(Capability.UploadOwnFiles);
    }

    /// <summary>
    /// One tile, from the printer's row and the two counts gathered for the whole page.
    /// </summary>
    /// <remarks>
    /// <b>What a disconnected printer may still say is the decision here.</b>
    /// <see cref="PrinterLiveState"/> persists, so every field on it survives the machine being
    /// switched off - and most of them stop being true the moment it is. Progress and the time left
    /// are frozen readings of a print nobody can see, so they are dropped; a tile reading "42%,
    /// 1:12 left" over an <i>Offline</i> badge is a page contradicting itself. The loaded filament is
    /// kept, because it is the one fact here that does not change while the power is out, and it is
    /// the thing worth knowing about a printer you are about to walk over to.
    /// </remarks>
    private PrinterShortcut ShortcutFor(PrinterWithState row,
                                        int recentJobs,
                                        IReadOnlyDictionary<int, int> queued,
                                        bool canPrint)
    {
        bool connected = _connections.IsConnected(row.Printer.Id);

        return new PrinterShortcut(
            row.Printer,
            PrinterDisplayName.For(row.Printer),
            connected,
            row.LiveState?.Status,
            PrinterFormFactors.For(row.LiveState),
            recentJobs,
            connected ? row.LiveState?.Progress : null,
            connected ? row.LiveState?.TimeRemaining : null,
            row.LiveState?.Material,
            queued.TryGetValue(row.Printer.Id, out int waiting) ? waiting : 0,
            canPrint,
            canPrint && row.Printer.RemoteReadyAllowed && connected);
    }
}
