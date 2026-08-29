using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Homespool.Host.PrintFiles.GCode;

/// <summary>
/// Reads the Printer Metadata block out of a binary G-code (<c>bgcode</c>) file, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cribbed from <c>libbgcode</c>'s <c>core.cpp</c></b> - the block ordering, the 8-versus-12 byte
/// block header and the payload-size arithmetic are its <c>is_valid_binary_gcode</c> walk, followed
/// literally rather than re-derived from the specification. Both projects are AGPL-3.0. There is no
/// .NET port of that library to depend on instead; the C# G-code packages on NuGet all parse ASCII
/// motion commands, which is the half this does not need.
/// </para>
/// <para>
/// <b>It stops at the third block, which is why it is cheap.</b> Printer Metadata is written second
/// or third (<c>doc/specifications.md</c>), and PrusaSlicer writes it <b>uncompressed</b> -
/// <c>GCodeProcessor.cpp</c>'s binarizer configuration sets <c>None</c> for it while the G-code
/// block gets Heatshrink. So the whole decompression apparatus this format can demand never comes
/// into it: a few KB from the head of the file answers the question, and <b>heatshrink is never
/// implemented</b> because it cannot occur on the block being read.
/// </para>
/// <para>
/// <b>Deflate is handled anyway</b>, as one call, because it costs a line and it is the one
/// compression a future writer could plausibly switch this block to. Heatshrink is reported as
/// unreadable rather than guessed at.
/// </para>
/// <para>
/// <b>Untrusted input.</b> Every size on the wire is attacker-influenced, so nothing is allocated
/// from a declared size without a bound, the walk is capped, and a malformed file yields
/// <c>null</c> rather than an exception.
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
    public static ReadOnlySpan<byte> Magic => "GCDE"u8;

    /// <summary>The version of the binarisation this understands.</summary>
    /// <remarks>
    /// A file declaring anything else is refused rather than read optimistically. The failure that
    /// buys is the safe one: an unreadable file says nothing, and a comparison with nothing to
    /// compare stays quiet, where a misread one would make claims about the wrong bytes.
    /// </remarks>
    private const uint SupportedVersion = 1;

    private const ushort FileMetadataBlock = 0;

    private const ushort GCodeBlock = 1;

    private const ushort PrinterMetadataBlock = 3;

    private const ushort ThumbnailBlock = 5;

    private const ushort NoCompression = 0;

    private const ushort DeflateCompression = 1;

    private const ushort IniEncoding = 0;

    /// <summary>
    /// Block parameters are two bytes of encoding for every block except a thumbnail, which carries
    /// format, width and height.
    /// </summary>
    private const int MetadataParametersSize = 2;

    private const int ThumbnailParametersSize = 6;

    /// <summary>
    /// How many blocks to walk before giving up. The block wanted is the second or third; anything
    /// past a handful means the file is not shaped the way the specification says.
    /// </summary>
    private const int MaxBlocksWalked = 8;

    /// <summary>
    /// Largest metadata block this will hold in memory. Two orders of magnitude above a real one -
    /// the block measures around 600 bytes on a single-object print - so this bounds an absurd
    /// declared size rather than constraining anything genuine.
    /// </summary>
    private const int MaxMetadataBytes = 1024 * 1024;

    /// <summary>
    /// What the file's Printer Metadata block said, an empty result if it carried none, or null if
    /// this is not a readable binary G-code file.
    /// </summary>
    /// <param name="stream">Positioned at the start of the file. Must be seekable.</param>
    public static GCodeMetadata? Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            return ReadCore(stream);
        }
        catch (EndOfStreamException)
        {
            // A truncated file, which is one of the ordinary shapes of corruption rather than an
            // exceptional condition: an upload that was interrupted has exactly this shape.
            return null;
        }
        catch (InvalidDataException)
        {
            // Deflate refusing a payload that claimed to be one.
            return null;
        }
        catch (IOException)
        {
            // The inflater's other refusal: zlib error codes .NET maps to an internal
            // ZLibException rather than InvalidDataException - a header demanding a preset
            // dictionary, for one. Found by fuzzing. Its only public ancestor is IOException,
            // which fits the contract regardless of what threw it: a file this cannot read
            // answers null, never an exception.
            return null;
        }
    }

    private static GCodeMetadata? ReadCore(Stream stream)
    {
        Span<byte> header = stackalloc byte[10];

        stream.Seek(0, SeekOrigin.Begin);
        stream.ReadExactly(header);

        if (!header[..4].SequenceEqual(Magic))
        {
            return null;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]) != SupportedVersion)
        {
            return null;
        }

        int checksumSize = BinaryPrimitives.ReadUInt16LittleEndian(header[8..10]) switch
        {
            0 => 0,
            1 => 4,

            // An algorithm this does not know is not merely an unverified checksum: its width is
            // what every subsequent block offset is computed from, so the walk cannot continue.
            _ => -1,
        };

        if (checksumSize < 0)
        {
            return null;
        }

        Span<byte> blockHeader = stackalloc byte[12];

        for (int walked = 0; walked < MaxBlocksWalked; walked++)
        {
            stream.ReadExactly(blockHeader[..8]);

            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(blockHeader[..2]);
            ushort compression = BinaryPrimitives.ReadUInt16LittleEndian(blockHeader[2..4]);
            uint uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(blockHeader[4..8]);
            uint dataSize = uncompressedSize;

            if (compression != NoCompression)
            {
                stream.ReadExactly(blockHeader[8..12]);
                dataSize = BinaryPrimitives.ReadUInt32LittleEndian(blockHeader[8..12]);
            }

            int parametersSize = type == ThumbnailBlock ? ThumbnailParametersSize : MetadataParametersSize;

            if (type == PrinterMetadataBlock)
            {
                return ReadPrinterMetadata(stream, compression, uncompressedSize, dataSize);
            }

            if (type == GCodeBlock)
            {
                // Past the point where it could still appear: the specification orders Printer
                // Metadata ahead of the thumbnails, the print and slicer metadata and the G-code. A
                // file that reaches here is well-formed and simply carries no printer metadata.
                return GCodeMetadata.Empty;
            }

            if (type is not (FileMetadataBlock or ThumbnailBlock) && type != PrinterMetadataBlock)
            {
                // Print or slicer metadata ahead of the printer block, or a type this does not know.
                // Either way the ordering is not what the walk assumes, so stop rather than guess.
                return GCodeMetadata.Empty;
            }

            // A skipped block's declared size is as attacker-influenced as any other, and a seek
            // target past 2^31 throws on an array-backed stream where a FileStream would tolerate
            // it. A block extending past the end of the file is truncation either way: refuse it
            // here rather than let the stream type decide between null and an exception.
            if (parametersSize + dataSize + checksumSize > stream.Length - stream.Position)
            {
                return null;
            }

            stream.Seek(parametersSize + dataSize + checksumSize, SeekOrigin.Current);
        }

        return GCodeMetadata.Empty;
    }

    private static GCodeMetadata? ReadPrinterMetadata(Stream stream,
                                                      ushort compression,
                                                      uint uncompressedSize,
                                                      uint dataSize)
    {
        Span<byte> parameters = stackalloc byte[MetadataParametersSize];

        stream.ReadExactly(parameters);

        if (BinaryPrimitives.ReadUInt16LittleEndian(parameters) != IniEncoding)
        {
            return null;
        }

        if (uncompressedSize > MaxMetadataBytes || dataSize > MaxMetadataBytes)
        {
            return null;
        }

        byte[] data = new byte[dataSize];

        stream.ReadExactly(data);

        string text;

        switch (compression)
        {
            case NoCompression:
                text = Encoding.UTF8.GetString(data);

                break;

            case DeflateCompression:
                // zlib-wrapped, not raw: libbgcode compresses with deflateInit rather than
                // deflateInit2, so the payload carries the two-byte zlib header.
                using (MemoryStream compressed = new(data))
                using (ZLibStream decompressor = new(compressed, CompressionMode.Decompress))
                {
                    // The declared size is the bound on the output, not a hint: deflate expands
                    // up to ~1000:1, so a stream read to its natural end would let a small upload
                    // allocate a gigabyte. A payload that stops short of its declaration or keeps
                    // going past it has lied about a size, and a lie about a size is malformed.
                    byte[] plain = new byte[uncompressedSize];
                    int read = decompressor.ReadAtLeast(plain, plain.Length, throwOnEndOfStream: false);

                    if (read < plain.Length || decompressor.ReadByte() != -1)
                    {
                        return null;
                    }

                    text = Encoding.UTF8.GetString(plain);
                }

                break;

            default:
                // Heatshrink. Unreachable on this block with every writer known today, and reported
                // as unreadable rather than approximated.
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
