using System;
using System.IO;

using libbgcode.NET;

namespace Homespool.Host.PrintFiles.GCode;

/// <summary>
/// Reads the Printer Metadata block out of a binary G-code (<c>bgcode</c>) file, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>The container is libbgcode.NET's business; this class is the policy around it.</b> The
/// library walks blocks, decompresses payloads and refuses malformed files; what stays here is
/// what a metadata probe wants: stop at the third block or so, spend at most a megabyte on a
/// payload, and turn the INI text into the five values the compatibility check compares.
/// </para>
/// <para>
/// <b>It stops early, which is why it is cheap.</b> Printer Metadata is written second or third
/// in the specification's ordering, ahead of the thumbnails and the G-code, so a few KB from the
/// head of the file answers the question and the megabytes behind it are never read.
/// </para>
/// <para>
/// <b>Three outcomes, matching the dispatcher's contract.</b> Metadata when the block is there
/// and readable; <see cref="GCodeMetadata.Empty"/> when the file is well-formed but does not
/// carry the block where it belongs; null when the file cannot be read - a refused header, a
/// malformed block, a payload the library declines.
/// </para>
/// </remarks>
internal static class BinaryGCodeMetadataReader
{
    /// <summary><c>GCDE</c>, the four bytes a binary G-code file starts with.</summary>
    /// <remarks>
    /// <b>The extension does not decide this and must not be consulted.</b> PrusaSlicer honours the
    /// printer profile's <c>binary_gcode</c> setting and not the name it was asked to write, so a
    /// file called <c>.gcode</c> is routinely binary - measured, not assumed, by slicing an MK3.5
    /// profile to a <c>.gcode</c> name and getting <c>GCDE</c>.
    /// </remarks>
    public static ReadOnlySpan<byte> Magic => BgcodeReader.Magic;

    /// <summary>
    /// How many blocks to walk before giving up. The block wanted is the second or third; anything
    /// past a handful means the file is not shaped the way the specification says.
    /// </summary>
    private const int MaxBlocksWalked = 8;

    /// <summary>
    /// Largest metadata payload this will hold in memory. Two orders of magnitude above a real one -
    /// the block measures around 600 bytes on a single-object print - so this bounds an absurd
    /// declared size rather than constraining anything genuine.
    /// </summary>
    private static readonly BgcodeReaderOptions Options = new() { MaxDataBytes = 1024 * 1024 };

    /// <summary>
    /// What the file's Printer Metadata block said, an empty result if it carried none, or null if
    /// this is not a readable binary G-code file.
    /// </summary>
    /// <param name="stream">Positioned at the start of the file. Must be seekable.</param>
    public static GCodeMetadata? Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        BgcodeReader? reader = BgcodeReader.Open(stream, Options);

        if (reader is null)
        {
            return null;
        }

        for (int walked = 0; walked < MaxBlocksWalked; walked++)
        {
            BgcodeBlock? block = reader.NextBlock();

            if (block is null)
            {
                // The clean end of a file with no printer metadata, or a malformed block - the
                // distinction the dispatcher's contract cares about.
                return reader.AtEnd ? GCodeMetadata.Empty : null;
            }

            if (block.Type == BgcodeBlockType.PrinterMetadata)
            {
                return ParsePrinterMetadata(reader, block);
            }

            if (block.Type == BgcodeBlockType.GCode)
            {
                // Past the point where it could still appear: the specification orders Printer
                // Metadata ahead of the thumbnails, the print and slicer metadata and the G-code. A
                // file that reaches here is well-formed and simply carries no printer metadata.
                return GCodeMetadata.Empty;
            }

            if (block.Type is not (BgcodeBlockType.FileMetadata or BgcodeBlockType.Thumbnail))
            {
                // Print or slicer metadata ahead of the printer block: the ordering is not what
                // the walk assumes, so stop rather than guess.
                return GCodeMetadata.Empty;
            }
        }

        return GCodeMetadata.Empty;
    }

    private static GCodeMetadata? ParsePrinterMetadata(BgcodeReader reader, BgcodeBlock block)
    {
        // Null for an encoding this does not know, a payload past the bound, or one that does not
        // decompress to its declared size - all of which make the file unreadable, not empty.
        string? text = reader.ReadText(block);

        if (text is null)
        {
            return null;
        }

        SlicerConfigValues values = new();

        foreach (ReadOnlySpan<char> line in text.AsSpan().EnumerateLines())
        {
            if (SlicerConfigValues.TrySplit(line.ToString(), out string key, out string value))
            {
                values.Accept(key, value);
            }

            if (values.Complete)
            {
                break;
            }
        }

        return values.Build();
    }
}
