using System;
using System.IO;

namespace Homespool.Host.PrintFiles.GCode;

/// <summary>
/// Reads what a print file says about the hardware it was sliced for, in either of the two
/// containers a printer accepts.
/// </summary>
/// <remarks>
/// <para>
/// <b>The container is decided by the file's first four bytes and never by its name.</b>
/// PrusaSlicer writes binary G-code whenever the printer profile says so, whatever extension it was
/// asked for, so a <c>.gcode</c> file is routinely <c>GCDE</c> - and this store accepts
/// <c>.gcode</c>, <c>.bgcode</c>, <c>.gco</c> and <c>.bgc</c> from anyone. Dispatching on the
/// extension would misread real files uploaded by ordinary means.
/// </para>
/// <para>
/// <b>Three outcomes, and the difference between the last two matters.</b> A file can say what it
/// was sliced for; it can be perfectly readable and say nothing, which is what output from another
/// slicer looks like; or it can be unreadable, which is corruption. The first two are recorded and
/// compared, the third is recorded as such - see <c>PrintFileMetadataState</c>. Collapsing "said
/// nothing" into "could not read" would make every non-PrusaSlicer upload look damaged.
/// </para>
/// </remarks>
public static class GCodeMetadataReader
{
    /// <summary>
    /// What <paramref name="stream"/> says about its hardware, or null if it could not be read as
    /// either container.
    /// </summary>
    /// <param name="stream">
    /// The whole file, seekable. Left at an unspecified position.
    /// </param>
    public static GCodeMetadata? Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanSeek)
        {
            throw new ArgumentException("The metadata readers seek to a header and a tail.", nameof(stream));
        }

        if (stream.Length < 4)
        {
            return null;
        }

        Span<byte> magic = stackalloc byte[4];

        stream.Seek(0, SeekOrigin.Begin);
        stream.ReadExactly(magic);

        return magic.SequenceEqual(BinaryGCodeMetadataReader.Magic) ?
            BinaryGCodeMetadataReader.Read(stream) :
            SlicerConfigMetadataReader.Read(stream);
    }

    /// <summary>Opens a file and reads it, or null if it cannot be opened or read.</summary>
    /// <remarks>
    /// A file that vanished between being listed and being read is an ordinary race here rather
    /// than an error: the store's reconcile exists because the filesystem is the truth and it moves
    /// under us.
    /// </remarks>
    public static GCodeMetadata? ReadFile(string path)
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
}
