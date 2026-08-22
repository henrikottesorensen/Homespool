using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Homespool.Host.Authorisation;
using Homespool.Host.Localisation;
using Homespool.Host.Printing;
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
/// is the exact complaint that started the printer-page rebuild - <c>notes/printer-page.md</c> §1,
/// "a page left open showed a live photograph beside a dead status" - and shipping a fresh one with
/// the same defect would be perverse when the machinery already exists.
/// </para>
/// </remarks>
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
    private readonly PrinterConnectionRegistry _connections;
    private readonly PrinterStatusText _statusText;
    private readonly UserManager<HSUser> _userManager;
    private readonly TimeProvider _clock;

    public IndexModel(PrinterQueryService printers,
                      PrintHistoryService history,
                      PrinterConnectionRegistry connections,
                      PrinterStatusText statusText,
                      UserManager<HSUser> userManager,
                      TimeProvider clock)
    {
        _printers = printers;
        _history = history;
        _connections = connections;
        _statusText = statusText;
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
    /// order. That is the rule <c>notes/printer-page.md</c> §6e was written for - a polled fragment
    /// may only render state its own handler loads - and here it also means a print started from
    /// another tab reorders the page by itself.
    /// </remarks>
    public async Task<IActionResult> OnGetTilesAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);

        return Partial("_PrinterShortcuts", this);
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

        // Printers you have never used stay in the list rather than being filtered out: a rack of
        // three where you have only ever used two should still show the third, or the front page
        // would hide a printer from the person most likely to be looking for it. They sort last, on
        // a zero count and a floor timestamp, which is exactly where "never used" belongs.
        Shortcuts = visible
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
                    .Select(entry => new PrinterShortcut(
                                entry.Row.Printer,
                                PrinterDisplayName.For(entry.Row.Printer),
                                _connections.IsConnected(entry.Row.Printer.Id),
                                entry.Row.LiveState?.Status,
                                PrinterFormFactors.For(entry.Row.LiveState),
                                entry.Usage.Jobs))
                    .ToList();
    }
}
