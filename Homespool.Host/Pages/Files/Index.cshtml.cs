using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

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
/// Talks to <see cref="UserFileStore"/> directly rather than to its own API over HTTP, as every
/// other page here talks to a service. The store is scoped by user id on every call, so the
/// ownership rule is the same one the API gets and is not restated here.
/// </para>
/// </remarks>
[Authorize]
[RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)] // Bounded by MaxUploadBytes below, not by MVC.
[RequestSizeLimit(long.MaxValue)]
public class IndexModel : PageModel
{
    private readonly UserFileStore _files;
    private readonly UserManager<HSUser> _userManager;
    private readonly PrintFileStorageOptions _options;

    public IndexModel(UserFileStore files, UserManager<HSUser> userManager, IOptions<PrintFileStorageOptions> options)
    {
        _files = files;
        _userManager = userManager;
        _options = options.Value;
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
        // alternative. Invariant culture so the separator does not move with the server's locale -
        // see notes/floating-point.md on the same hazard in Razor.
        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{size:0.#} {units[unit]}");
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
    public bool NextDescendingFor(string column) =>
        column == Sort ? !Descending : column != Columns.Name;

    /// <summary>The arrow for a heading: direction when it is the sorted column, nothing when it is not.</summary>
    public string IndicatorFor(string column) =>
        column != Sort ? string.Empty : Descending ? " ↓" : " ↑";

    public void OnGet(string? sort, bool desc, string? rename)
    {
        Load(sort, desc);

        // Only offer to rename something that is actually there, so a stale link is an ordinary page
        // rather than an input editing nothing.
        Renaming = rename is not null && Files.Any(file => string.Equals(file.FileName, rename, StringComparison.OrdinalIgnoreCase))
            ? rename
            : null;
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
    public async Task<IActionResult> OnPostUploadAsync(IFormFile? file, string? sort, bool desc,
        CancellationToken cancellationToken)
    {
        long? userId = UserId();

        if (userId is null)
        {
            return Forbid();
        }

        if (file is null || file.Length == 0)
        {
            (StatusMessage, StatusSuccess) = ("Choose a file to upload.", false);

            return RedirectToSelf(sort, desc);
        }

        if (file.Length > _options.MaxUploadBytes)
        {
            // Known before a byte is copied, because the request has already been buffered - so this
            // refuses without writing anything, which the streaming version could not have done.
            (StatusMessage, StatusSuccess) =
                ($"That file is larger than the {FormatSize(_options.MaxUploadBytes)} limit.", false);

            return RedirectToSelf(sort, desc);
        }

        PendingUpload staged;

        try
        {
            await using Stream content = file.OpenReadStream();

            staged = await _files.StageAsync(userId.Value, file.FileName, content, cancellationToken);
        }
        catch (ArgumentException e)
        {
            (StatusMessage, StatusSuccess) = (e.Message, false);

            return RedirectToSelf(sort, desc);
        }

        try
        {
            StoredFile? stored = _files.Publish(userId.Value, staged.Token, overwrite: false);

            (StatusMessage, StatusSuccess) = ($"Uploaded {stored!.FileName}.", true);
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
    public IActionResult OnPostReplace(string token, string? sort, bool desc)
    {
        long? userId = UserId();

        if (userId is null)
        {
            return Forbid();
        }

        StoredFile? stored = _files.Publish(userId.Value, token, overwrite: true);

        (StatusMessage, StatusSuccess) = stored is null
            ? ("That upload is no longer waiting - it may have been cleared up. Try again.", false)
            : ($"Replaced {stored.FileName}.", true);

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

        _files.Discard(userId.Value, token);
        (StatusMessage, StatusSuccess) = ("Upload discarded.", true);

        return RedirectToSelf(sort, desc);
    }

    public IActionResult OnPostRename(string name, string newName, string? sort, bool desc)
    {
        long? userId = UserId();

        if (userId is null)
        {
            // [Authorize] should make this unreachable; fail closed rather than act on an invented id.
            return Forbid();
        }

        try
        {
            StoredFile? renamed = _files.Rename(userId.Value, name, newName ?? string.Empty);

            (StatusMessage, StatusSuccess) = renamed is null
                ? ($"There is no file named {name}.", false)
                : ($"Renamed to {renamed.FileName}.", true);
        }
        catch (PrintFileNameConflictException e)
        {
            (StatusMessage, StatusSuccess) = (e.Message, false);
        }
        catch (ArgumentException e)
        {
            (StatusMessage, StatusSuccess) = (e.Message, false);
        }

        return RedirectToSelf(sort, desc);
    }

    public IActionResult OnPostDelete(string name, string? sort, bool desc)
    {
        long? userId = UserId();

        if (userId is null)
        {
            return Forbid();
        }

        (StatusMessage, StatusSuccess) = _files.Delete(userId.Value, name)
            ? ($"Deleted {name}.", true)
            : ($"There is no file named {name}.", false);

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
    private IActionResult RedirectToSelf(string? sort, bool desc) =>
        RedirectToPage(new { sort, desc });

    private long? UserId() =>
        long.TryParse(_userManager.GetUserId(User), CultureInfo.InvariantCulture, out long id) ? id : null;

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

        IReadOnlyList<StoredFile> files = _files.List(userId.Value);

        IOrderedEnumerable<StoredFile> ordered = Sort switch
        {
            Columns.Size => desc
                ? files.OrderByDescending(file => file.Length)
                : files.OrderBy(file => file.Length),
            Columns.Uploaded => desc
                ? files.OrderByDescending(file => file.UploadedAt)
                : files.OrderBy(file => file.UploadedAt),
            _ => desc
                ? files.OrderByDescending(file => file.FileName, StringComparer.OrdinalIgnoreCase)
                : files.OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase),
        };

        // Ordering is presentation, so it happens here rather than in the store, which keeps
        // returning one stable name-ordered list whoever asks.
        Files = ordered.ToList();
    }
}
