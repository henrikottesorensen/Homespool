using System;
using System.IO;
using System.Text;

namespace Homespool.Host.PrintFiles.GCode;

/// <summary>
/// Reads the configuration block PrusaSlicer appends to an ASCII G-code file.
/// </summary>
/// <remarks>
/// <para>
/// <b>The block is at the end of the file</b>, delimited by two phony keys - <c>prusaslicer_config
/// = begin</c> and <c>= end</c> (<c>GCode.cpp</c>) - so this reads a tail rather than streaming the
/// whole file. That is the cheap half of a decision worth stating: the upload already streams every
/// byte past a hash, so a line scanner could ride along on that pass, but it would put a parser in
/// the middle of the upload path to save a read of the last few hundred KB of a file that is
/// already on local disk.
/// </para>
/// <para>
/// <b>256 KB of tail, where Prusa's own reader takes 40 KB</b> (<c>gcode-metadata</c>'s
/// <c>METADATA_END_OFFSET</c>). Measured at 15 KB for a single-object print; the margin is for a
/// configuration carrying five filaments and a long custom start G-code, and the cost of being
/// generous is one read of a page cache.
/// </para>
/// </remarks>
internal static class SlicerConfigMetadataReader
{
    private const int TailBytes = 256 * 1024;

    private const string BeginMarker = "; prusaslicer_config = begin";

    private const string EndMarker = "; prusaslicer_config = end";

    /// <summary>
    /// What the file's configuration block said, or an empty result if it carries none - which is
    /// the ordinary answer for output from a slicer that is not PrusaSlicer.
    /// </summary>
    /// <param name="stream">The file. Must be seekable.</param>
    public static GCodeMetadata Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        long length = stream.Length;
        int take = (int)Math.Min(length, TailBytes);

        stream.Seek(length - take, SeekOrigin.Begin);

        byte[] tail = new byte[take];

        stream.ReadExactly(tail);

        // A tail can start mid-character, which the decoder turns into one replacement character.
        // Harmless: the marker searched for is ASCII and everything parsed comes after it.
        string text = Encoding.UTF8.GetString(tail);
        int begin = text.LastIndexOf(BeginMarker, StringComparison.Ordinal);

        if (begin < 0)
        {
            return GCodeMetadata.Empty;
        }

        SlicerConfigValues values = new();

        foreach (ReadOnlySpan<char> rawLine in text.AsSpan(begin + BeginMarker.Length).EnumerateLines())
        {
            ReadOnlySpan<char> line = rawLine.Trim();

            if (line.StartsWith(EndMarker, StringComparison.Ordinal))
            {
                break;
            }

            if (line.Length == 0 || line[0] != ';')
            {
                continue;
            }

            if (SlicerConfigValues.TrySplit(line[1..].ToString(), out string key, out string value))
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
