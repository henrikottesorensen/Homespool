using System;
using System.Collections.Concurrent;
using System.IO;

namespace Homespool.Host.Firmware;

/// <summary>
/// What the verifier said about each stored image, as long as its file is the one it said it about:
/// the same length and the same last write.
/// </summary>
/// <remarks>
/// <para>
/// <b>For listing only.</b> The firmware page lists every stored image on every load, and verifying one
/// reads it whole - an older release's littlefs images included - so without this each visit paid for
/// every image again. A file written since is verified again; the length and the write time are what
/// any change through the file system moves.
/// </para>
/// <para>
/// <b>Flashing and deleting do not use it</b>: they verify the file as it is now, so a file changed
/// underneath in a way that kept its length and write time may still be listed, but is never sent.
/// </para>
/// </remarks>
public sealed class FirmwareCheckCache
{
    private readonly ConcurrentDictionary<string, Remembered> _checks = new(StringComparer.Ordinal);

    /// <summary>The verdict remembered for <paramref name="file"/>, if it is still the same file.</summary>
    public PrusaFirmwareCheck? For(FileInfo file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return _checks.TryGetValue(file.FullName, out Remembered? remembered) &&
               remembered.Length == file.Length && remembered.WrittenAt == file.LastWriteTimeUtc ?
            remembered.Check :
            null;
    }

    /// <summary>Remembers <paramref name="check"/> for <paramref name="file"/> as it is now.</summary>
    public void Remember(FileInfo file, PrusaFirmwareCheck check)
    {
        ArgumentNullException.ThrowIfNull(file);

        _checks[file.FullName] = new Remembered(file.Length, file.LastWriteTimeUtc, check);
    }

    private sealed record Remembered(long Length, DateTime WrittenAt, PrusaFirmwareCheck Check);
}
