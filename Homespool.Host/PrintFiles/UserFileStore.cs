using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.Exceptions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homespool.Host.PrintFiles;

/// <summary>
/// Each user's print files, as a plain directory of files on local disk.
/// </summary>
/// <remarks>
/// <para>
/// <b>The layout is <c>{root}/{userId}/{name}</c>, and that is the whole design</b>
/// (<c>notes/file-storage.md</c>). The user's mental model is a folder of files, so the store is
/// one: names are unique per user and are the identity, <c>ls</c> shows you exactly what a user has,
/// and ownership is <i>where a file lives</i> rather than a column somebody forgets to check. Every
/// lookup here is scoped to a user id, so there is no way to ask for a file without saying whose.
/// </para>
/// <para>
/// <b>Replaces the id-addressed store</b> that came before it, which was a testing surface and said
/// so: it never expired anything, deduplicated nothing, and handed any file to whoever held its
/// opaque id. Its identifier was content-shaped so that one value could serve as file handle
/// <i>and</i> transfer token inside firmware's 28-character budget - the constraint that shaped, and
/// misshaped, everything else. The token is now minted per transfer and thrown away, so storage owes
/// the wire nothing.
/// </para>
/// <para>
/// <b>No database table, deliberately, and not forever.</b> The filesystem is the truth; a table
/// would today be an index with no reader, since size and timestamp come from the entry itself and
/// the surrogate id a table would add exists to protect machinery - a queue, job history - that does
/// not exist yet. When it does, the table joins and this class keeps its shape.
/// </para>
/// <para>
/// <b>Names are compared case-insensitively</b>, though the filesystem underneath may not be. macOS
/// folds case and Linux does not, so identical code would accept <c>Benchy.gcode</c> beside
/// <c>benchy.gcode</c> in production and reject it on a developer's laptop - and the printer's FAT32
/// would then collide the two at <c>/usb/</c> anyway. Deciding it here makes the behaviour the same
/// everywhere.
/// </para>
/// </remarks>
[SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities",
                 Justification = "Every caller-supplied name is reduced by SafeName to Path.GetFileName and then refused if it is empty, '.' or '..', so no input can express a directory or traverse upwards. The other path component is a user id, which is an integer this process read from its own database.")]
public sealed class UserFileStore
{
    /// <summary>
    /// Where a file is assembled before it is given its name. A sibling of the user directories
    /// rather than a child, so a half-written upload can never appear in anyone's listing.
    /// </summary>
    /// <remarks>
    /// It has to live under the same root for the rename that publishes it to be atomic: that is
    /// <c>rename(2)</c>, which works across directories but not across filesystems. A leading dot
    /// also keeps it out of the way of user directories, which are digits.
    /// </remarks>
    private const string IncomingDirectory = ".incoming";

    /// <summary>
    /// Print files, and <b>deliberately less than the printer will accept</b>. This is a security
    /// boundary, not a convenience.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Connect transfer is gated by firmware's <c>filename_is_transferrable</c>, which is
    /// <c>filename_is_printable || filename_is_firmware</c> — and <c>filename_is_firmware</c> is
    /// <c>.bbf</c>. So the printer would accept a <b>firmware image</b> as a transfer destination, and
    /// this list is what stops an authenticated user putting one there.
    /// </para>
    /// <para>
    /// <b>Why a firmware image matters:</b> <c>M997</c> reflashes the mainboard from a <c>.bbf</c>
    /// under <c>/usb/</c>, and the application validates nothing before rebooting into the bootloader
    /// to do it. So the chain is <i>upload a <c>.bbf</c></i> + <i>send <c>M997</c></i>. The second half
    /// is currently impossible — <see cref="PrusaConnect.Commands.GCode"/> is a hollow marker and cannot be sent —
    /// but the two guards are independent and neither knows about the other. <b>If gcode ever becomes
    /// sendable, this list is the only remaining barrier.</b> That reasoning is written up at the other
    /// end too, on <see cref="PrusaConnect.Commands.GCode"/>, because that is the end someone will actually touch.
    /// </para>
    /// <para>
    /// <b>Do not widen this because the printer turns out to accept more.</b> It does, and that is the
    /// reason the list is narrow. An earlier comment here claimed anything else "is rejected by the
    /// printer after a pointless upload and transfer", which invited exactly that change.
    /// </para>
    /// <para>
    /// Also narrower than <c>filename_is_printable</c>, which additionally allows <c>.g</c> and
    /// <c>.gc</c>. That gap is harmless and unconsidered rather than deliberate.
    /// </para>
    /// </remarks>
    private static readonly string[] AllowedExtensions = [".gcode", ".bgcode", ".gco", ".bgc"];

    private readonly string _root;
    private readonly ILogger<UserFileStore> _logger;

    public UserFileStore(IOptions<PrintFileStorageOptions> options, IHostEnvironmentAccessor environment,
        ILogger<UserFileStore> logger)
    {
        _root = Path.IsPathRooted(options.Value.Directory)
            ? options.Value.Directory
            : Path.Combine(environment.ContentRootPath, options.Value.Directory);
        _logger = logger;
    }

    /// <summary>Whether the name ends in something a printer would accept.</summary>
    public static bool IsAllowedExtension(string fileName) =>
        AllowedExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    /// <summary>Everything <paramref name="userId"/> has uploaded, oldest name first.</summary>
    /// <remarks>
    /// Reads the directory rather than a table, which is what makes the filesystem the truth: a file
    /// that is there is listed, and one that is not cannot be. A user who has uploaded nothing has no
    /// directory yet, and gets an empty list rather than an error.
    /// </remarks>
    public IReadOnlyList<StoredFile> List(long userId)
    {
        string directory = DirectoryFor(userId);

        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory)
            .Select(Describe)
            .OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Resolves one of <paramref name="userId"/>'s files by name, or null if they have no
    /// such file.</summary>
    /// <remarks>
    /// Matching is case-insensitive, so the answer does not depend on which filesystem this is
    /// running on. The returned <see cref="StoredFile.FileName"/> is the name <i>on disk</i>, not the
    /// spelling that was asked for.
    /// </remarks>
    public StoredFile? Find(long userId, string fileName)
    {
        string? safeName = SafeName(fileName);

        if (safeName is null)
        {
            return null;
        }

        string directory = DirectoryFor(userId);

        if (!Directory.Exists(directory))
        {
            return null;
        }

        string? path = Directory.EnumerateFiles(directory)
            .FirstOrDefault(candidate => string.Equals(Path.GetFileName(candidate), safeName,
                StringComparison.OrdinalIgnoreCase));

        return path is null ? null : Describe(path);
    }

    /// <summary>
    /// Streams <paramref name="content"/> into <paramref name="userId"/>'s directory under
    /// <paramref name="fileName"/>.
    /// </summary>
    /// <param name="userId">Whose file this is. Becomes the directory it lives in.</param>
    /// <param name="fileName">
    /// The name to keep, which is also the name the file takes on the printer. Only its file-name
    /// part is used - any directory component is discarded rather than sanitised, so no input can
    /// escape the store.
    /// </param>
    /// <param name="content">The bytes, streamed rather than buffered.</param>
    /// <param name="overwrite">
    /// Replace an existing file of that name. Without it an existing name is a
    /// <see cref="PrintFileNameConflictException"/> rather than a silent replacement.
    /// </param>
    /// <param name="cancellationToken">Abandons a partial upload if the client disconnects.</param>
    /// <exception cref="ArgumentException">The name is empty, or not one a printer would accept.</exception>
    /// <exception cref="PrintFileNameConflictException">The name is taken and <paramref name="overwrite"/> is false.</exception>
    /// <remarks>
    /// <para>
    /// <b>Written to <see cref="IncomingDirectory"/> and renamed into place at the end</b>, never
    /// written at its final name. Three things follow: a cancelled or failed upload leaves nothing
    /// behind to be listed or printed; an overwrite is a single atomic replacement, so a reader sees
    /// the old bytes or the new ones and never a torn mixture; and the new content lands on a
    /// <i>new</i> inode, which is what lets a transfer already in flight keep serving the bytes it
    /// announced (<see cref="PrusaConnect.Transfers.FileTransferContent"/>).
    /// </para>
    /// <para>
    /// The name is checked twice on purpose. The check before the upload fails a doomed request
    /// before hundreds of megabytes cross the wire; the rename at the end is the one that is
    /// authoritative, because two uploads of the same name can race between them.
    /// </para>
    /// </remarks>
    public async Task<StoredFile> SaveAsync(long userId, string fileName, Stream content, bool overwrite,
        CancellationToken cancellationToken)
    {
        string safeName = RequireSafeName(fileName);

        if (!IsAllowedExtension(safeName))
        {
            // Also checked by the endpoint, which can say it better. Repeated here because this is
            // the boundary that must hold whoever calls it - see AllowedExtensions.
            throw new ArgumentException($"'{safeName}' is not a file a printer would accept.", nameof(fileName));
        }

        if (!overwrite && Find(userId, safeName) is not null)
        {
            throw new PrintFileNameConflictException(safeName);
        }

        string directory = DirectoryFor(userId);
        string incoming = Path.Combine(_root, IncomingDirectory);

        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(incoming);

        string temporary = Path.Combine(incoming, Guid.NewGuid().ToString("N"));

        try
        {
            await using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true))
            {
                await content.CopyToAsync(file, cancellationToken);
            }

            // An existing file has to go before the rename when the caller asked for that, because
            // File.Move(overwrite: true) would happily replace one they did not ask about - the
            // case-insensitive match may be spelled differently from the name we are about to write.
            StoredFile? existing = Find(userId, safeName);

            if (existing is not null && !overwrite)
            {
                throw new PrintFileNameConflictException(safeName);
            }

            string path = Path.Combine(directory, safeName);

            if (existing is not null && !string.Equals(existing.Path, path, StringComparison.Ordinal))
            {
                File.Delete(existing.Path);
            }

            File.Move(temporary, path, overwrite);

            StoredFile stored = Describe(path);
            _logger.LogInformation("Stored {FileName} for user {UserId} ({Length} bytes){Replaced}",
                stored.FileName, userId, stored.Length, existing is null ? string.Empty : ", replacing");

            return stored;
        }
        catch
        {
            DeleteQuietly(temporary);

            throw;
        }
    }

    /// <summary>Renames one of <paramref name="userId"/>'s files.</summary>
    /// <exception cref="ArgumentException">The new name is empty, or not one a printer would accept.</exception>
    /// <exception cref="PrintFileNameConflictException">Another file already has the new name.</exception>
    /// <remarks>
    /// Possible at all only because identity is the name rather than the content: under the
    /// id-addressed store that came before, a rename meant re-uploading. Anything that must survive
    /// one - a queue entry, job history - references the file by something other than its name, which
    /// is the reason those references will not be names.
    /// </remarks>
    public StoredFile? Rename(long userId, string fileName, string newName)
    {
        string safeNewName = RequireSafeName(newName);

        if (!IsAllowedExtension(safeNewName))
        {
            throw new ArgumentException($"'{safeNewName}' is not a file a printer would accept.", nameof(newName));
        }

        StoredFile? existing = Find(userId, fileName);

        if (existing is null)
        {
            return null;
        }

        string path = Path.Combine(DirectoryFor(userId), safeNewName);

        // Comparing paths rather than names so that changing only the case of a file's own name is a
        // rename and not a collision with itself.
        StoredFile? occupant = Find(userId, safeNewName);

        if (occupant is not null && !string.Equals(occupant.Path, existing.Path, StringComparison.Ordinal))
        {
            throw new PrintFileNameConflictException(safeNewName);
        }

        File.Move(existing.Path, path, overwrite: true);
        _logger.LogInformation("Renamed {FileName} to {NewName} for user {UserId}",
            existing.FileName, safeNewName, userId);

        return Describe(path);
    }

    /// <summary>
    /// Deletes one of <paramref name="userId"/>'s files, and reports whether there was one.
    /// </summary>
    /// <remarks>
    /// The lifecycle's third verb, missing until now, which is how a store that nothing ever removed
    /// from accumulated 21 stale directories. A transfer already reading this file is unharmed: its
    /// handle keeps the bytes alive until it is done (<see cref="PrusaConnect.Transfers.FileTransferContent"/>).
    /// </remarks>
    public bool Delete(long userId, string fileName)
    {
        StoredFile? existing = Find(userId, fileName);

        if (existing is null)
        {
            return false;
        }

        File.Delete(existing.Path);
        _logger.LogInformation("Deleted {FileName} for user {UserId}", existing.FileName, userId);

        return true;
    }

    private static StoredFile Describe(string path)
    {
        FileInfo info = new(path);

        return new StoredFile(info.Name, path, info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>
    /// Reduces a caller's name to something that cannot express a path, or null if nothing is left.
    /// </summary>
    /// <remarks>
    /// <c>Path.GetFileName</c> discards any directory part, which handles <c>../../etc/passwd</c> and
    /// absolute paths alike. What it does not handle is a name that <i>is</i> a directory reference:
    /// it returns <c>..</c> unchanged, and that one would traverse. Hence the explicit refusal.
    /// </remarks>
    private static string? SafeName(string fileName)
    {
        string name = Path.GetFileName(fileName);

        if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..")
        {
            return null;
        }

        return name;
    }

    private static string RequireSafeName(string fileName) =>
        SafeName(fileName) ?? throw new ArgumentException(
            "File name is empty, or is a directory reference, once its directory part is removed.",
            nameof(fileName));

    /// <summary>Deletes a path if it is there, and never throws for a path that is not.</summary>
    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Cleaning up after a failure must not replace the failure with a different one.
        }
    }

    private string DirectoryFor(long userId) =>
        Path.Combine(_root, userId.ToString(CultureInfo.InvariantCulture));
}

/// <summary>One of a user's files.</summary>
/// <param name="FileName">The name as stored, which is also the name it takes on the printer.</param>
/// <param name="Path">Absolute path on this machine.</param>
/// <param name="Length">Size in bytes, which becomes a transfer's <c>orig_size</c>.</param>
/// <param name="UploadedAt">When it was last written, which for an overwrite is when it was replaced.</param>
public sealed record StoredFile(string FileName, string Path, long Length, DateTimeOffset UploadedAt)
{
    /// <summary>
    /// Where this file lands on the printer, and therefore what a print command needs to run it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The convention lives here rather than at the call sites so there is one answer: the
    /// <c>path</c> we put in <c>START_CONNECT_DOWNLOAD</c>, and the value reported to whoever
    /// uploaded the file. It is the same string for every printer, which is why it belongs on the
    /// stored file rather than on a transfer.
    /// </para>
    /// <para>
    /// <b>Not converted to an 8.3 short name, deliberately.</b>
    /// <c>MarlinPrinter::start_print</c> hands our path to <c>print_begin</c> unchanged
    /// (marlin_printer.cpp:540 at the pinned ref), so the long name is what a printer accepts;
    /// <c>is_sfn</c> describes how it <i>reports</i> paths, not what it takes - the conversion is on
    /// the reporting side only (<c>get_SFN_path</c>, render.cpp:1134). Confirmed on hardware rather
    /// than inferred: the captures show <c>/usb</c> running FAT32 with long-name support live, a
    /// 57-character name written and then reported back as <c>LAMPEN~2.BGC</c>. Deriving a short name
    /// ourselves would mean inventing the <c>~N</c> collision index against directory contents we
    /// cannot see, and a wrong guess prints a different file. See
    /// <c>notes/protocol-reference.md</c>, "<c>FILE_INFO</c> vs <c>FILE_CHANGED</c>" and "The
    /// captures, fully decoded".
    /// </para>
    /// </remarks>
    public string PrinterPath => $"/usb/{FileName}";
}
