using System;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homespool.Host.PrusaConnect.Transfers;

/// <summary>
/// Uploaded gcode on local disk, addressed by an opaque id.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a database entity. An upload is a file, the filesystem stores files, and adding
/// a table would mean a migration plus two places that can disagree about what exists. Each upload
/// gets its own directory named by its id, holding the file under its original name - so the id
/// resolves to both the path and the name it should keep on the printer, with nothing to keep in
/// step.
/// </para>
/// <para>
/// The consequence to be aware of: nothing expires. This is a testing surface
/// (<c>notes/transfer-protocol.md</c>, T3), and retention belongs with whatever real file management
/// replaces it.
/// </para>
/// </remarks>
[SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities",
                 Justification = "Both inputs are constrained before use: the file name is reduced to Path.GetFileName, discarding any directory part, and the hash must be 27 characters from the base64url alphabet, which contains no separator and no dot. Neither can express a path.")]
public class UploadedFileStore
{
    /// <summary>
    /// 20 bytes, base64url'd to 27 characters - deliberately the same shape and size as Connect's own
    /// file ids, and inside firmware's 28-character hash ceiling
    /// (<see cref="Commands.StartConnectDownload.MaxHashLength"/>) so it can be handed to a printer
    /// verbatim.
    /// </summary>
    /// <remarks>
    /// One identifier serves as both the file handle and the transfer token, which is how Connect
    /// works: the <c>hash</c> its app API takes for <c>start/cloud</c> is the same one the printer
    /// quotes back on its first range request. 160 bits because it is a capability as much as a name.
    /// </remarks>
    private const int HashBytes = 20;

    /// <summary>Length of <see cref="HashBytes"/> bytes once base64url-encoded, unpadded.</summary>
    private const int HashLength = 27;

    /// <summary>
    /// Print files, and <b>deliberately less than the printer will accept</b>. This is a security
    /// boundary, not a convenience.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Connect transfer is gated by firmware's <c>filename_is_transferrable</c>, which is
    /// <c>filename_is_printable || filename_is_firmware</c> — and <c>filename_is_firmware</c> is
    /// <c>.bbf</c>. So the printer would accept a <b>firmware image</b> as a transfer destination, and
    /// this list is what stops an authenticated user putting one there through
    /// <c>command/start/cloud</c>.
    /// </para>
    /// <para>
    /// <b>Why a firmware image matters:</b> <c>M997</c> reflashes the mainboard from a <c>.bbf</c>
    /// under <c>/usb/</c>, and the application validates nothing before rebooting into the bootloader
    /// to do it. So the chain is <i>upload a <c>.bbf</c></i> + <i>send <c>M997</c></i>. The second half
    /// is currently impossible — <see cref="Commands.GCode"/> is a hollow marker and cannot be sent —
    /// but the two guards are independent and neither knows about the other. <b>If gcode ever becomes
    /// sendable, this list is the only remaining barrier.</b> That reasoning is written up at the other
    /// end too, on <see cref="Commands.GCode"/>, because that is the end someone will actually touch.
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
    private readonly ILogger<UploadedFileStore> _logger;

    public UploadedFileStore(IOptions<FileStorageOptions> options, IHostEnvironmentAccessor environment,
        ILogger<UploadedFileStore> logger)
    {
        _root = Path.IsPathRooted(options.Value.Directory)
            ? options.Value.Directory
            : Path.Combine(environment.ContentRootPath, options.Value.Directory);
        _logger = logger;
    }

    /// <summary>Whether the name ends in something a printer would accept.</summary>
    public static bool IsAllowedExtension(string fileName) =>
        AllowedExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Streams <paramref name="content"/> to disk under a fresh id, and returns it.
    /// </summary>
    /// <param name="fileName">
    /// The name to keep. Only its file-name part is used - any directory component a caller supplies
    /// is discarded rather than sanitised, so no input can escape the store's root.
    /// </param>
    /// <param name="content">The bytes, streamed rather than buffered.</param>
    /// <param name="cancellationToken">Abandons a partial upload if the client disconnects.</param>
    public async Task<StoredFile> SaveAsync(string fileName, Stream content, CancellationToken cancellationToken)
    {
        string safeName = Path.GetFileName(fileName);

        if (string.IsNullOrWhiteSpace(safeName))
        {
            throw new ArgumentException("File name is empty once its directory part is removed.", nameof(fileName));
        }

        string id = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(HashBytes));
        string directory = Path.Combine(_root, id);

        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, safeName);

        // FileMode.CreateNew: the id is fresh, so an existing file means something is very wrong and
        // silently overwriting it would be the worst answer.
        await using (FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, useAsync: true))
        {
            await content.CopyToAsync(file, cancellationToken);
        }

        long length = new FileInfo(path).Length;
        _logger.LogInformation("Stored upload {Id} ({FileName}, {Length} bytes)", id, safeName, length);

        return new StoredFile(id, safeName, path, length);
    }

    private static bool IsBase64UrlCharacter(char c) =>
        char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_';

    /// <summary>Resolves an id to what was stored under it, or null if nothing was.</summary>
    public StoredFile? Find(string id)
    {
        // The id goes into a path, so it must not be able to contain one. The base64url alphabet is
        // letters, digits, '-' and '_' - no separator, no dot - so a value that passes this cannot
        // traverse, whatever else it is.
        if (id.Length != HashLength || !id.All(IsBase64UrlCharacter))
        {
            return null;
        }

        string directory = Path.Combine(_root, id);

        if (!Directory.Exists(directory))
        {
            return null;
        }

        string? path = Directory.EnumerateFiles(directory).FirstOrDefault();

        return path is null ? null : new StoredFile(id, Path.GetFileName(path), path, new FileInfo(path).Length);
    }
}

/// <summary>One stored upload.</summary>
/// <param name="Id">Opaque handle, and the directory name it lives in.</param>
/// <param name="FileName">The name as uploaded, which is also the name it takes on the printer.</param>
/// <param name="Path">Absolute path on this machine.</param>
/// <param name="Length">Size in bytes, which becomes the command's <c>orig_size</c>.</param>
public sealed record StoredFile(string Id, string FileName, string Path, long Length)
{
    /// <summary>
    /// Where this file lands on the printer, and therefore what <c>command/start/files</c> needs to
    /// print it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The convention lives here rather than at the two call sites so there is one answer: the
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

/// <summary>
/// The content root, behind an interface so a component that keeps files can be constructed in a test
/// without an <c>IWebHostEnvironment</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolve every relative path through this, never through <c>IWebHostEnvironment</c> directly.</b>
/// It is the single seam where "a directory in configuration" becomes "a directory on disk", which is
/// what lets a test point the whole application somewhere harmless in one move
/// (<c>HomespoolFactory</c>). Under <c>WebApplicationFactory</c> the real content root is the
/// <i>project directory</i>, so a component that resolves its own path against
/// <c>IWebHostEnvironment</c> writes into the working tree of whoever runs the suite - which the
/// database, uploaded gcode and the printer certificate each did in turn, and each was found by its
/// own separate symptom.
/// </para>
/// <para>
/// Nothing enforces this: a new component can inject <c>IWebHostEnvironment</c> and nobody would
/// notice until files appeared in <c>Homespool.Host/data</c>. <c>ContentRootIsolationTests</c> asserts
/// the two current consumers still resolve here, which is the closest thing to a guard there is.
/// </para>
/// </remarks>
public interface IHostEnvironmentAccessor
{
    string ContentRootPath { get; }
}
