using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
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

    /// <summary>Upload only: the bytes land in the reader's tree and nothing else happens.</summary>
    public const string DropUpload = "upload";

    /// <summary>Upload, then join the printer's queue.</summary>
    public const string DropQueue = "queue";

    /// <summary>
    /// Upload, queue, and offer the printer up for work.
    /// </summary>
    /// <remarks>
    /// <b>There is no "start printing" command behind this, and there should not be.</b>
    /// <see cref="Queue.QueueRules.IsAvailable"/> admits exactly one status, <c>Ready</c>, and the
    /// advancer picks the head up within about a second of it. So readying a printer whose queue this
    /// drop just filled <i>is</i> printing now. A direct start would be a second path to the same
    /// place that skipped the rule standing between a queue and a print onto somebody's finished part.
    /// </remarks>
    public const string DropReadyAndPrint = "ready";

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
    private readonly PrintFileCatalog _files;
    private readonly CameraAccessService _cameras;
    private readonly PrintFileStorageOptions _storage;
    private readonly PrinterCommandService _commands;
    private readonly PrinterConnectionRegistry _connections;
    private readonly PrinterStatusText _statusText;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ErrorText _errors;
    private readonly UserManager<HSUser> _userManager;
    private readonly TimeProvider _clock;

    public IndexModel(PrinterQueryService printers,
                      PrintHistoryService history,
                      PrintQueueService queue,
                      PrinterAccessService access,
                      PrintFileCatalog files,
                      CameraAccessService cameras,
                      IOptionsSnapshot<PrintFileStorageOptions> storage,
                      PrinterCommandService commands,
                      PrinterConnectionRegistry connections,
                      PrinterStatusText statusText,
                      IStringLocalizer<SharedResource> localiser,
                      ErrorText errors,
                      UserManager<HSUser> userManager,
                      TimeProvider clock)
    {
        _printers = printers;
        _history = history;
        _queue = queue;
        _access = access;
        _files = files;
        _cameras = cameras;
        _storage = storage.Value;
        _commands = commands;
        _connections = connections;
        _statusText = statusText;
        _localiser = localiser;
        _errors = errors;
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

    /// <summary>
    /// Whether the reader may overwrite one of their own files, which is a capability of its own -
    /// <see cref="Capability.ManipulateOwnFiles"/> rather than
    /// <see cref="Capability.UploadOwnFiles"/>. Somebody able to add files but not change them gets
    /// the name-clash question with only one answer available, and the dialog says so rather than
    /// offering a replace that would be refused.
    /// </summary>
    public bool CanReplace { get; private set; }

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
    /// Answers what a drop would collide with, before it uploads anything.
    /// </summary>
    /// <remarks>
    /// <b>Names in, a rendered dialog out.</b> It could answer JSON and let the browser build the
    /// dialog, and then every word in it would need a second copy of the vocabulary out here - the
    /// same trade <c>live-region.js</c> refuses at the top of its file. The browser sends names and
    /// gets back markup it can show.
    /// </remarks>
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

        // The reader's own tree, so the comparison never sees anybody else's names. Ordinal-ignore-case
        // because that is what the store treats as the same file.
        HashSet<string> existing = _files.List(caller)
                                         .Select(stored => stored.FileName)
                                         .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool canPrint = await _access.AllowsAsync(row.Printer.Id, caller, Capability.Print, cancellationToken);

        TileDropPrompt prompt = new(
            uuid,
            PrinterDisplayName.For(row.Printer),
            [.. names.Select(name => new TileDropFile(name,
                                     existing.Contains(name),
                                     UserFileStore.IsAllowedExtension(name)))],
            canPrint,
            canPrint && row.Printer.RemoteReadyAllowed && _connections.IsConnected(row.Printer.Id),
            caller.Allows(Capability.ManipulateOwnFiles),
            await CameraFrameUrlAsync(row.Printer.Id, caller, cancellationToken),
            string.Join(", ", UserFileStore.AllowedExtensions),
            Files.IndexModel.FormatSize(_storage.MaxUploadBytes));

        return Partial("_TileDrop", prompt);
    }

    /// <summary>
    /// Carries out a drop: upload each file, then queue, then optionally make the printer ready.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Per-file reporting, because upload-then-queue can half-succeed.</b> A file can land and
    /// then be refused a place in the queue - a hold, a nozzle mismatch, a model incompatibility - so
    /// one summary line would be a lie for whichever half failed. Each file gets its own sentence.
    /// </para>
    /// <para>
    /// <b>A list, though a drag only ever sends one.</b> The single-file rule lives in tile-drop.js,
    /// where the drop happens; this is a handler anybody can post to, and looping is what keeps it
    /// honest rather than dependent on the caller having obeyed a convention it cannot see.
    /// </para>
    /// <para>
    /// <b>Readying happens last, and only if something was queued.</b> Making a printer ready with
    /// nothing at the head of its queue offers the machine up for work that is not there; doing it
    /// first would let the loop pick up an unrelated older entry the moment it went ready, which is a
    /// print nobody asked for starting because of a drop that then failed.
    /// </para>
    /// <para>
    /// <b>Ready is still guarded here</b> even though the dialog only offers it when allowed. The
    /// browser decides what to show; it does not decide what may happen.
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

        bool queueing = action is DropQueue or DropReadyAndPrint;
        bool readying = action == DropReadyAndPrint;

        if (readying && !row.Printer.RemoteReadyAllowed)
        {
            return Forbid();
        }

        HashSet<string> replacing = replace.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> report = [];
        int queued = 0;
        bool refused = false;

        foreach (IFormFile file in files)
        {
            if (file.Length == 0)
            {
                continue;
            }

            string? stored = await StoreAsync(caller, file, replacing.Contains(file.FileName), report, cancellationToken);

            if (stored is null)
            {
                continue;
            }

            if (!queueing)
            {
                // Said out loud, because an upload-only drop otherwise finishes in silence and looks
                // exactly like a drop that missed the tile. StoreAsync only reports what went wrong.
                report.Add(_localiser["Files_UploadedFile", stored].Value);

                continue;
            }

            try
            {
                await _queue.EnqueueAsync(row.Printer.Id, caller, stored, cancellationToken);
                queued++;
                report.Add(_localiser["Home_DropQueued", stored, PrinterDisplayName.For(row.Printer)].Value);
            }
            catch (Exception e) when (e is ILocalisableError)
            {
                // The bytes are safely in the reader's tree either way, so this reports the queue's
                // refusal and leaves the file alone rather than undoing an upload that was fine.
                report.Add(_localiser["Home_DropQueueRefused", stored, _errors.For(e)].Value);
            }
        }

        if (readying && queued > 0)
        {
            try
            {
                await _commands.SendCommandAsync(row.Printer.Id, new SetPrinterReady(), caller, cancellationToken);

                // The printer page's own words for this, unchanged: the queue line above already
                // named the printer, so repeating it here would be a second voice.
                report.Add(_localiser["Printers_ReadySent"].Value);
            }
            catch (Exception e) when (e is ILocalisableError)
            {
                // The upload and the queue already happened and are worth keeping - this is the last
                // step of three, and losing the first two because the third failed would be worse
                // than saying so. Uncaught, it escaped as a 500 and the person got a blank page
                // having no idea their file had in fact been queued.
                report.Add(_errors.For(e));
                refused = true;
            }
        }

        // A drop where every file was refused before it started - all of them the wrong kind, or an
        // empty selection - would otherwise redirect to a page saying nothing at all.
        StatusMessage = report.Count > 0 ? string.Join(" ", report) : _localiser["Home_DropNothing"].Value;
        StatusSuccess = report.Count > 0 && !refused && (queued > 0 || !queueing);

        return RedirectToPage();
    }

    /// <summary>
    /// Puts one dropped file in the reader's tree, reporting what happened to it.
    /// </summary>
    /// <remarks>
    /// Staged then published, the same two steps the Files page uses. The name clash was settled
    /// before any of this ran, so <paramref name="overwrite"/> is an answer already given rather than
    /// a question asked here - but the store is still the authority, and a file that appeared between
    /// the question and now comes back as a conflict and is reported rather than silently replaced.
    /// </remarks>
    private async Task<string?> StoreAsync(Caller caller,
                                           IFormFile file,
                                           bool overwrite,
                                           List<string> report,
                                           CancellationToken cancellationToken)
    {
        PendingUpload staged;

        try
        {
            await using Stream content = file.OpenReadStream();

            staged = await _files.StageAsync(caller, file.FileName, content, cancellationToken);
        }
        catch (Exception e) when (e is ArgumentException or ILocalisableError)
        {
            report.Add(_localiser["Home_DropRejected", file.FileName, _errors.For(e)].Value);

            return null;
        }

        try
        {
            StoredFile? published = await _files.PublishAsync(caller, staged.Token, overwrite, cancellationToken, userName: UserNameOf());

            return published?.FileName;
        }
        catch (PrintFileNameConflictException)
        {
            // Keep the file already on disk, and throw away the bytes just staged rather than leaving
            // them to age out - the reader answered this question before the upload started, so there
            // is nothing left to ask and nothing to keep them for.
            _files.Discard(caller, staged.Token);

            report.Add(_localiser["Home_DropKept", file.FileName].Value);

            return staged.FileName;
        }
    }

    private string? UserNameOf()
    {
        return User.Identity?.Name;
    }

    /// <summary>
    /// A still of the printer for the bed-clear question, or null when it has no camera.
    /// </summary>
    /// <remarks>
    /// The first camera, matching what the printer page's Set ready modal shows. A printer with two
    /// cameras has one that answers "is the sheet clear" better than the other, and nothing here
    /// knows which - so this takes the same one that page takes rather than inventing a preference.
    /// </remarks>
    private async Task<string?> CameraFrameUrlAsync(int printerId, Caller caller, CancellationToken cancellationToken)
    {
        IReadOnlyList<Camera> cameras = await _cameras.ListForPrinterAsync(printerId, caller, cancellationToken);

        return cameras.Count == 0 ? null : Url.Action("Frame", "Camera", new { uuid = cameras[0].Uuid });
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
        CanReplace = caller.Allows(Capability.ManipulateOwnFiles);
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
