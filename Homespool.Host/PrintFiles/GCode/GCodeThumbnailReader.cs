using System;
using System.Buffers.Text;
using System.Globalization;
using System.IO;
using System.Text;

using libbgcode.NET;

namespace Homespool.Host.PrintFiles.GCode;

/// <summary>
/// The largest PNG preview a print file carries, in either container, or null.
/// </summary>
/// <remarks>
/// <para>
/// <b>PNG only, and that is not a loss.</b> PrusaSlicer writes several previews into every file, each
/// for a different reader: small QOI images for the printer's own screens, which no browser decodes,
/// and one PNG - 380x285 in every file measured - which is the one firmware attaches when it describes
/// a file to Connect (<c>render.cpp</c> takes the first PNG larger than 17x17). So the PNG is the
/// picture meant for a web page, and it is served as it is stored.
/// </para>
/// <para>
/// <b>The largest PNG rather than the first</b>, because a profile can be set to write more than one,
/// and the page shows it at half a screen's width.
/// </para>
/// <para>
/// <b>Both containers keep their previews at the head of the file</b>, so neither walk reads past it:
/// a binary file orders them after the printer metadata and ahead of everything else, and a plain one
/// writes them as comments before the first G-code command.
/// </para>
/// </remarks>
public static class GCodeThumbnailReader
{
    /// <summary>
    /// Largest image this will hold. The measured one is about 53 KB; this bounds a declared size in a
    /// file nobody vouches for rather than constraining a real preview.
    /// </summary>
    public const int MaxImageBytes = 1024 * 1024;

    /// <summary>
    /// How many blocks to walk. File and printer metadata, then the previews - four from a current
    /// PrusaSlicer profile - so anything past this is not a file shaped the way the specification says.
    /// </summary>
    private const int MaxBlocksWalked = 32;

    /// <summary>
    /// How much of a plain file's head to read before giving up on reaching its first command. The
    /// measured header, previews included, is about 190 KB.
    /// </summary>
    private const int MaxHeadChars = 4 * 1024 * 1024;

    /// <summary>The opening line of a PNG preview in a plain file, up to its size.</summary>
    private const string PngBeginTag = "; thumbnail begin ";

    private static readonly BgcodeReaderOptions Options = new() { MaxDataBytes = MaxImageBytes };

    /// <summary>The eight bytes every PNG starts with.</summary>
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The preview's bytes, or null when the file has no PNG preview or cannot be read.</summary>
    /// <param name="stream">The whole file, seekable. Left at an unspecified position.</param>
    public static byte[]? Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanSeek)
        {
            throw new ArgumentException("The binary reader seeks back to the block it chose.", nameof(stream));
        }

        if (stream.Length < 4)
        {
            return null;
        }

        Span<byte> magic = stackalloc byte[4];

        stream.Seek(0, SeekOrigin.Begin);
        stream.ReadExactly(magic);
        stream.Seek(0, SeekOrigin.Begin);

        byte[]? image = magic.SequenceEqual(BgcodeReader.Magic) ? ReadBinary(stream) : ReadPlain(stream);

        // What is served is what was checked: the response says image/png whatever the file claimed.
        return image is not null && image.AsSpan().StartsWith(PngSignature) ? image : null;
    }

    /// <summary>Opens a file and reads it, or null if it cannot be opened or read.</summary>
    /// <remarks>
    /// A file deleted or replaced between being found and being opened is an ordinary race, not an
    /// error: the answer is simply that there is no preview to show.
    /// </remarks>
    public static byte[]? ReadFile(string path)
    {
        try
        {
            using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            return Read(file);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static byte[]? ReadBinary(Stream stream)
    {
        BgcodeReader? reader = BgcodeReader.Open(stream, Options);

        if (reader is null)
        {
            return null;
        }

        BgcodeBlock? best = null;

        for (int walked = 0; walked < MaxBlocksWalked; walked++)
        {
            BgcodeBlock? block = reader.NextBlock();

            // The end of the file, a malformed block, or the first block past the previews: in each
            // case every preview there is has been seen.
            if (block is null ||
                block.Type is not (BgcodeBlockType.FileMetadata or BgcodeBlockType.PrinterMetadata or BgcodeBlockType.Thumbnail))
            {
                break;
            }

            if (block.Thumbnail is { Format: BgcodeThumbnailFormat.Png } candidate &&
                (best?.Thumbnail is not { } chosen || Area(candidate.Width, candidate.Height) > Area(chosen.Width, chosen.Height)))
            {
                best = block;
            }
        }

        // Read last, because reading seeks: the walk cannot go on from wherever a payload left it.
        return best is null ? null : reader.ReadData(best);
    }

    /// <summary>
    /// The largest <c>; thumbnail begin WxH N</c> section, decoded. The <c>_QOI</c> and <c>_JPG</c>
    /// variants name themselves in the tag and are stepped over.
    /// </summary>
    private static byte[]? ReadPlain(Stream stream)
    {
        using StreamReader reader = new(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        StringBuilder? capturing = null;
        long capturingArea = 0;
        string? bestBase64 = null;
        long bestArea = 0;
        long charsRead = 0;

        while (reader.ReadLine() is { } line)
        {
            charsRead += line.Length + 1;

            if (charsRead > MaxHeadChars)
            {
                break;
            }

            if (capturing is not null)
            {
                if (line.StartsWith("; thumbnail end", StringComparison.Ordinal))
                {
                    if (capturingArea > bestArea)
                    {
                        bestBase64 = capturing.ToString();
                        bestArea = capturingArea;
                    }

                    capturing = null;
                }
                else if (line.StartsWith("; ", StringComparison.Ordinal))
                {
                    capturing.Append(line.AsSpan(2).Trim());

                    // A section that never ends would otherwise grow to the head's bound.
                    if (capturing.Length > Base64.GetMaxEncodedToUtf8Length(MaxImageBytes))
                    {
                        capturing = null;
                    }
                }
                else
                {
                    capturing = null;
                }

                continue;
            }

            if (TryParsePngBegin(line, out long area))
            {
                // Only a section that would win is kept, so at most one image is ever held.
                capturing = area > bestArea ? new StringBuilder() : null;
                capturingArea = area;

                continue;
            }

            // The first command ends the header, and the previews with it.
            if (line.Length > 0 && line[0] != ';')
            {
                break;
            }
        }

        if (bestBase64 is null)
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(bestBase64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>; thumbnail begin WxH N</c>, the PNG form - PrusaSlicer's tag carries no format suffix for
    /// PNG, and <c>thumbnail_QOI</c> and <c>thumbnail_JPG</c> for the others.
    /// </summary>
    private static bool TryParsePngBegin(string line, out long area)
    {
        area = 0;

        if (!line.StartsWith(PngBeginTag, StringComparison.Ordinal))
        {
            return false;
        }

        string[] words = line[PngBeginTag.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length < 1)
        {
            return false;
        }

        string[] size = words[0].Split('x');

        if (size.Length != 2 ||
            !ushort.TryParse(size[0], NumberStyles.None, CultureInfo.InvariantCulture, out ushort width) ||
            !ushort.TryParse(size[1], NumberStyles.None, CultureInfo.InvariantCulture, out ushort height))
        {
            return false;
        }

        area = Area(width, height);

        return true;
    }

    private static long Area(ushort width, ushort height)
    {
        return (long)width * height;
    }
}
