using System;
using System.Collections.Generic;
using System.Globalization;
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
using Microsoft.Extensions.Logging;
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

namespace Homespool.Host.Pages.Files;

/// <summary>
/// The signed-in user's print files: what they have, and the three things they can do to one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The page this app was missing.</b> Uploading, renaming and deleting have been reachable only
/// through <c>/api/v1/files</c> since the store was rewritten, which meant a file a user uploaded
/// was invisible to them - the exact confusion that made overwriting opt-in in the first place
/// (<c>notes/file-storage.md</c>).
/// </para>
/// <para>
/// Talks to <see cref="PrintFileCatalog"/> directly rather than to its own API over HTTP, as every
/// other page here talks to a service. Every call is scoped by user id, so the ownership rule is the
/// same one the API gets and is not restated here. (It went through <see cref="UserFileStore"/> until
/// the file index arrived; the catalog is that store plus the row, so that nothing on this page can
/// change a file without the two staying in step.)
/// </para>
/// </remarks>
[Authorize]
[RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)] // Bounded by MaxUploadBytes below, not by MVC.
[RequestSizeLimit(long.MaxValue)]
public class IndexModel : PageModel
{
    private readonly PrintFileCatalog _files;
    private readonly UserManager<HSUser> _userManager;
    private readonly PrintFileStorageOptions _options;
    private readonly PrinterQueryService _printers;
    private readonly PrintFileSender _sender;
    private readonly PrintQueueService _queue;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ErrorText _errors;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(PrintFileCatalog files,
                      UserManager<HSUser> userManager,
                      IOptions<PrintFileStorageOptions> options,
                      PrinterQueryService printers,
                      PrintFileSender sender,
                      PrintQueueService queue,
                      IStringLocalizer<SharedResource> localiser,
                      ErrorText errors,
                      ILogger<IndexModel> logger)
    {
        _files = files;
        _userManager = userManager;
        _options = options.Value;
        _printers = printers;
        _sender = sender;
        _queue = queue;
        _localiser = localiser;
        _errors = errors;
        _logger = logger;
    }

    /// <summary>The columns that can be sorted on, as they appear in the query string.</summary>
    public static class Columns
    {
        public const string Name = "name";
        public const string Size = "size";
        public const string Uploaded = "uploaded";
    }

    public IReadOnlyList<StoredFile> Files { get; private set; } = [];

    /// <summary>Which column the table is ordered by, one of <see cref="Columns"/>.</summary>
    public string Sort { get; private set; } = Columns.Uploaded;

    public bool Descending { get; private set; } = true;

    /// <summary>
    /// The file whose name is being edited, if any.
    /// </summary>
    /// <remarks>
    /// Renaming is a query-string mode rather than a JavaScript toggle: following the Rename link
    /// reloads the page with that row's name replaced by an input. It keeps the page free of script
    /// like every other page here, and makes a half-finished rename survive a refresh.
    /// </remarks>
    public string? Renaming { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public bool StatusSuccess { get; set; }

    /// <summary>
    /// The staged upload waiting on an answer to "replace the file you already have?", if any.
    /// </summary>
    /// <remarks>
    /// Carried in TempData rather than re-posted, because the bytes are already on disk and this is
    /// only the handle to them. It survives exactly one redirect, which is the life of the question.
    /// </remarks>
    [TempData]
    public string? PendingToken { get; set; }

    [TempData]
    public string? PendingName { get; set; }

    /// <summary>Largest upload accepted, for the hint under the file picker.</summary>
    public long MaxUploadBytes => _options.MaxUploadBytes;

    /// <summary>
    /// The printers this user can see, for the per-row send control. Empty means the control is not
    /// rendered at all - offering a select with nothing in it explains nothing.
    /// </summary>
    public IReadOnlyList<Printer> Printers { get; private set; } = [];

    /// <summary>Bytes as a person reads them. Binary units, because that is what a printer's storage uses.</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        int unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        // No decimal on bytes, one everywhere else: "512 B" and "4.1 MB" both read better than the
        // alternative.
        //
        // The reader's culture, not the invariant one. This was invariant "so the separator does not
        // move with the server's locale", which was the right instinct answered on the wrong axis:
        // the danger was never that a Danish *server* would render 4,1 MB to an English reader, it
        // was that the separator would follow the machine rather than the person. Now that a request
        // carries the reader's culture, following it is what puts the separator where they read it -
        // and 4,1 MB is simply how a number is written in Danish, so invariant here means being
        // wrong for them on purpose.
        //
        // notes/floating-point.md records the hazard this replaced, and is about precision rather
        // than culture; the two are separate questions on the same value.
        return unit == 0 ?
            string.Create(CultureInfo.CurrentCulture, $"{bytes} B") :
            string.Create(CultureInfo.CurrentCulture, $"{size:0.#} {units[unit]}");
    }

    /// <summary>
    /// Where a column heading links to: the same column flips direction, a different one starts at
    /// whichever direction is useful for it.
    /// </summary>
    /// <remarks>
    /// Names read forwards and the other two read backwards - the newest upload and the biggest file
    /// are what someone is looking for, while <c>a.gcode</c> is where an alphabetical list should
    /// start. Always-ascending would make two of the three headings need a second click to be useful.
    /// </remarks>
    public bool NextDescendingFor(string column)
    {
        return column == Sort ? !Descending : column != Columns.Name;
    }

    /// <summary>The arrow for a heading: direction when it is the sorted column, nothing when it is not.</summary>
    public string IndicatorFor(string column)
    {
        return column != Sort ? string.Empty : Descending ? " ↓" : " ↑";
    }

    public async Task OnGetAsync(string? sort, bool desc, string? rename, CancellationToken cancellationToken)
    {
        Load(sort, desc);
        await LoadPrintersAsync(cancellationToken);

        // Only offer to rename something that is actually there, so a stale link is an ordinary page
        // rather than an input editing nothing.
        Renaming =
            rename is not null && Files.Any(file => string.Equals(file.FileName, rename, StringComparison.OrdinalIgnoreCase)) ?
                rename :
                null;
    }

    /// <summary>
    /// Takes an upload, and either stores it or asks whether to replace what is already there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bytes are kept while the question is asked.</b> A clash is only knowable once the file
    /// has arrived, so discarding it would make the answer "yes, replace it" cost a second upload of
    /// the same file. Staging it instead makes that answer free, at the price of holding the bytes
    /// until the question is answered or swept (<c>notes/file-storage.md</c>).
    /// </para>
    /// <para>
    /// <b>Bound as <see cref="IFormFile"/> rather than read with <c>MultipartReader</c></b>, which
    /// was the original intention. Streaming the sections consumes the request body, and
    /// <c>IAntiforgery.ValidateRequestAsync</c> reads the form to find its token - so a streamed
    /// upload cannot validate an antiforgery token that came from a plain HTML form. Microsoft's own
    /// streaming sample sends the token in a header from JavaScript, which this app has none of.
    /// What buffering actually costs here is one extra write: ASP.NET spills past
    /// <c>MemoryBufferThreshold</c> to a temp file on disk, not into memory.
    /// </para>
    /// </remarks>
    public async Task<IActionResult> OnPostUploadAsync(IFormFile? file,
                                                       string? sort,
                                                       bool desc,
                                                       CancellationToken cancellationToken)
    {
        long? userId = UserId();

        if (userId is null)
        {
            return Forbid();
        }

        if (file is null || file.Length == 0)
        {
            (StatusMessage, StatusSuccess) = (_localiser["Files_ChooseFile"], false);

            return RedirectToSelf(sort, desc);
        }

        if (file.Length > _options.MaxUploadBytes)
        {
            // Known before a byte is copied, because the request has already been buffered - so this
            // refuses without writing anything, which the streaming version could not have done.
            (StatusMessage, StatusSuccess) =
                (_localiser["Files_TooLarge", FormatSize(_options.MaxUploadBytes)], false);

            return RedirectToSelf(sort, desc);
        }

        PendingUpload staged;

        try
        {
            await using Stream content = file.OpenReadStream();

            staged = await _files.StageAsync(CallerResolver.For(userId.Value, User), file.FileName, content, cancellationToken);
        }
        catch (ArgumentException e)
        {
            (StatusMessage, StatusSuccess) = (_errors.For(e), false);

            return RedirectToSelf(sort, desc);
        }

        try
        {
            StoredFile? stored = await _files.PublishAsync(CallerResolver.For(userId.Value, User), staged.Token, overwrite: false,
                                                           cancellationToken, UserName());

            (StatusMessage, StatusSuccess) = (_localiser["Files_UploadedFile", stored!.FileName], true);
        }
        catch (PrintFileNameConflictException)
        {
            // Kept, not discarded: answering the question below is what decides its fate.
            PendingToken = staged.Token;
            PendingName = staged.FileName;
        }

        return RedirectToSelf(sort, desc);
    }

    /// <summary>Answers the replace question with yes, using bytes already on disk.</summary>
    public async Task<IActionResult> OnPostReplaceAsync(string token,
                                                        string? sort,
                                                        bool desc,
                                                        CancellationToken cancellationToken)
    {
        long? userId = UserId();

        if (userId is null)
        {
            return Forbid();
        }

        StoredFile? stored = await _files.PublishAsync(CallerResolver.For(userId.Value, User), token, overwrite: true, cancellationToken,
                                                       UserName());

        (StatusMessage, StatusSuccess) = stored is null ?
            (_localiser["Files_UploadGone"], false) :
            (_localiser["Files_Replaced", stored.FileName], true);

        return RedirectToSelf(sort, desc);
    }

    /// <summary>Answers it with no, and throws the staged bytes away now rather than at the sweep.</summary>
    public IActionResult OnPostDiscard(string token, string? sort, bool desc)
    {
        long? userId = UserId();

        if (userId is null)
        {
            return Forbid();
        }

        _files.Discard(CallerResolver.For(userId.Value, User), token);
        (StatusMessage, StatusSuccess) = (_localiser["Files_Discarded"], true);

        return RedirectToSelf(sort, desc);
    }

    /// <summary>
    /// Tells a printer to come and fetch one of the caller's files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same three steps the API endpoint takes, through the same
    /// <see cref="PrintFileSender"/> - so the rule that a send which did not take leaves no offer
    /// behind has one implementation rather than two that drift.
    /// </para>
    /// <para>
    /// <b>Answers when the printer accepts the command, not when the transfer finishes.</b> A
    /// full-size model takes minutes to move, so the message here says it has started; the page has
    /// no way to show progress yet (<c>notes/file-storage.md</c>).
    /// </para>
    /// </remarks>
    /// <summary>
    /// Adds a file to a printer's queue, which is the other thing to do with a printer and a file.
    /// </summary>
    /// <remarks>
    /// <b>Queueing is not sending.</b> Send moves the bytes now and the printer starts nothing;
    /// queue writes a row and moves nothing, leaving the printer's producer loop to transfer and
    /// print it when the printer is actually free. Two buttons rather than one because they answer
    /// different questions - "put this on the printer" and "print this next" - and collapsing them
    /// would mean guessing which was meant.
    /// </remarks>
    public async Task<IActionResult> OnPostQueueAsync(string name,
                                                      int printerId,
                                                      string? sort,
                                                      bool desc,
                                                      CancellationToken cancellationToken)
    {
        long? userId = UserId();

        if (userId is null)
        {
            return Forbid();
        }

        // Same ownership rule as Send: the printer has to be one of the caller's own, found in the
        // list already scoped to them rather than fetched by the id the form supplied.
        await LoadPrintersAsync(cancellationToken);

        if (!Printers.Any(candidate => candidate.Id == printerId))
        {
            (StatusMessage, StatusSuccess) = (_localiser["Files_PrinterNotYours"], false);

            return RedirectToSelf(sort, desc);
        }

        try
        {
            EnqueueOutcome outcome =
                await _queue.EnqueueAsync(printerId, CallerResolver.For(userId.Value, User), name, cancellationToken);

            // Queued either way - the loop is what stops a print that must not happen, and this is
            // the moment to say so while somebody is still looking at the screen.
            (StatusMessage, StatusSuccess) = outcome.Warnings.Count == 0 ?
                (_localiser["Files_Queued", name], true) :
                (string.Join(' ', outcome.Warnings.Select(_errors.For)),
                 outcome.Severity != PrintCompatibilitySeverity.Hold);
        }
        catch (PrintFileNotFoundException e)
        {
            (StatusMessage, StatusSuccess) = (_errors.For(e), false);
        }
        catch (TeamAccessDeniedException)
        {
            (StatusMessage, StatusSuccess) = (_localiser["Files_PrinterReadOnly"], false);
        }

        return RedirectToSelf(sort, desc);
    }

    public async Task<IActionResult> OnPostSendAsync(string name,
                                                     int printerId,
                                                     string? sort,
                                                     bool desc,
                                                     CancellationToken cancellationToken)
    {
        long? userId = UserId();

        if (userId is null)
        {
            return Forbid();
        }

        StoredFile? file = _files.Find(CallerResolver.For(userId.Value, User), name);

        if (file is null)
        {
            (StatusMessage, StatusSuccess) = (_localiser["Files_NoSuchFile", name], false);

            return RedirectToSelf(sort, desc);
        }

        if (file.Length >= uint.MaxValue)
        {
            // orig_size is uint32 on the wire; a file this large cannot be described at all.
            (StatusMessage, StatusSuccess) = (_localiser["Files_OverFourGiB"], false);

            return RedirectToSelf(sort, desc);
        }

        // Looked up in the caller's own list rather than fetched by the id the form supplied. That
        // list is already scoped to them, so this *is* the ownership check - a printer id typed into
        // the form by hand finds nothing rather than someone else's machine.
        await LoadPrintersAsync(cancellationToken);

        Printer? printer = Printers.FirstOrDefault(candidate => candidate.Id == printerId);

        if (printer is null)
        {
            (StatusMessage, StatusSuccess) = (_localiser["Files_PrinterNotYours"], false);

            return RedirectToSelf(sort, desc);
        }

        try
        {
            CommandOutcome? outcome = (await _sender.SendAsync(printer, file, CallerResolver.For(userId.Value, User), cancellationToken)).Outcome;

            (StatusMessage, StatusSuccess) =
                outcome?.EventType is PrinterEventType.Rejected or PrinterEventType.Failed ?
                    (_localiser["Files_Refused", PrinterName(printer), outcome!.Reason ?? string.Empty], false) :
                    (_localiser["Files_Sending", file.FileName, PrinterName(printer)], true);
        }
        catch (PrintFileUnreadableException e)
        {
            (StatusMessage, StatusSuccess) = (_errors.For(e), false);
        }
        catch (PrinterNotConnectedException)
        {
            (StatusMessage, StatusSuccess) = (_localiser["Files_PrinterNotConnected", PrinterName(printer)], false);
        }
        catch (CommandAlreadyInFlightException)
        {
            (StatusMessage, StatusSuccess) = (_localiser["Files_PrinterBusy", PrinterName(printer)], false);
        }
        catch (CommandResponseTimedOutException)
        {
            (StatusMessage, StatusSuccess) = (_localiser["Files_PrinterNoAnswer", PrinterName(printer)], false);
        }
        catch (TeamAccessDeniedException)
        {
            (StatusMessage, StatusSuccess) = (_localiser["Files_NoPermission"], false);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            // The same backstop Pages/Printers/Index keeps, and for the same reason: a socket write
            // racing a disconnect surfaces as whatever the socket layer produced rather than as one
            // of the typed exceptions above, and an unhandled 500 is a worse answer than a sentence.
            _logger.LogWarning(e, "Sending {FileName} to printer {PrinterId} failed unexpectedly", file.FileName, printer.Id);

            (StatusMessage, StatusSuccess) = (_localiser["Files_SendFailed"], false);
        }

        return RedirectToSelf(sort, desc);
    }

    public async Task<IActionResult> OnPostRenameAsync(string name,
                                                       string newName,
                                                       string? sort,
                                                       bool desc,
                                                       CancellationToken cancellationToken)
    {
        long? userId = UserId();

        if (userId is null)
        {
            // [Authorize] should make this unreachable; fail closed rather than act on an invented id.
            return Forbid();
        }

        try
        {
            StoredFile? renamed =
                await _files.RenameAsync(CallerResolver.For(userId.Value, User), name, newName ?? string.Empty, cancellationToken);

            (StatusMessage, StatusSuccess) = renamed is null ?
                (_localiser["Files_NoSuchFile", name], false) :
                (_localiser["Files_Renamed", renamed.FileName], true);
        }
        catch (PrintFileNameConflictException e)
        {
            (StatusMessage, StatusSuccess) = (_errors.For(e), false);
        }
        catch (ArgumentException e)
        {
            (StatusMessage, StatusSuccess) = (_errors.For(e), false);
        }

        return RedirectToSelf(sort, desc);
    }

    /// <summary>
    /// Deletes a file, unless a queued print still wants it.
    /// </summary>
    /// <remarks>
    /// The refusal is not an obstacle to route around: a printer's queue is shared, so the print this
    /// would cancel may not be the deleter's own. Cancelling the queued print is the deliberate act that
    /// unblocks it - see <see cref="PrintFileCatalog.DeleteAsync"/>.
    /// </remarks>
    public async Task<IActionResult> OnPostDeleteAsync(string name,
                                                       string? sort,
                                                       bool desc,
                                                       CancellationToken cancellationToken)
    {
        long? userId = UserId();

        if (userId is null)
        {
            return Forbid();
        }

        (StatusMessage, StatusSuccess) = await _files.DeleteAsync(CallerResolver.For(userId.Value, User), name, cancellationToken) switch
        {
            PrintFileDeletion.Deleted => (_localiser["Files_Deleted", name], true),
            PrintFileDeletion.Queued => (_localiser["Files_QueuedCannotDelete", name], false),
            _ => (_localiser["Files_NoSuchFile", name], false),
        };

        return RedirectToSelf(sort, desc);
    }

    /// <summary>
    /// Back to the list, keeping the order the user chose.
    /// </summary>
    /// <remarks>
    /// Carrying the sort through the redirect is the whole reason every handler takes it: without it
    /// the table silently jumps back to its default after each delete, which feels broken and reads
    /// as a bug nobody can quite describe.
    /// </remarks>
    /// <summary>
    /// What to call a printer in a message. The same fallback chain <c>Pages/Printers/Index</c>
    /// uses, and for the reason documented on <see cref="Printer.Name"/>: the uuid is the only part
    /// that cannot be missing.
    /// </summary>
    private static string PrinterName(Printer printer)
    {
        return printer.Name ?? printer.Model ?? printer.Uuid.ToString();
    }

    private IActionResult RedirectToSelf(string? sort, bool desc)
    {
        return RedirectToPage(new { sort, desc });
    }

    private async Task LoadPrintersAsync(CancellationToken cancellationToken)
    {
        long? userId = UserId();

        Printers = userId is null ? [] : await _printers.ListPrintersForUserAsync(CallerResolver.For(userId.Value, User), cancellationToken);
    }

    private long? UserId()
    {
        return long.TryParse(_userManager.GetUserId(User), CultureInfo.InvariantCulture, out long id) ? id : null;
    }

    /// <summary>
    /// The username that decorates this user's storage directory, read from the sign-in cookie rather
    /// than the database - Identity puts it there, and an upload has no reason to pay for a query to
    /// learn something the request already carries.
    /// </summary>
    private string? UserName()
    {
        return _userManager.GetUserName(User);
    }

    private void Load(string? sort, bool desc)
    {
        long? userId = UserId();

        if (userId is null)
        {
            Files = [];

            return;
        }

        // An unrecognised column is the default rather than an error: it can only come from a
        // hand-edited query string, and there is nothing useful to say about it.
        Sort = sort switch
        {
            Columns.Name => Columns.Name,
            Columns.Size => Columns.Size,
            _ => Columns.Uploaded,
        };

        Descending = desc;

        IReadOnlyList<StoredFile> files = _files.List(CallerResolver.For(userId.Value, User));

        IOrderedEnumerable<StoredFile> ordered = Sort switch
        {
            Columns.Size => desc ? files.OrderByDescending(file => file.Length) : files.OrderBy(file => file.Length),
            Columns.Uploaded => desc ? files.OrderByDescending(file => file.UploadedAt) : files.OrderBy(file => file.UploadedAt),
            _ => desc ?
                files.OrderByDescending(file => file.FileName, StringComparer.OrdinalIgnoreCase) :
                files.OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase),
        };

        // Ordering is presentation, so it happens here rather than in the store, which keeps
        // returning one stable name-ordered list whoever asks.
        Files = ordered.ToList();
    }
}
