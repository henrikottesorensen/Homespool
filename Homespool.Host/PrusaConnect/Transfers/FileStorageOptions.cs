namespace Homespool.Host.PrusaConnect.Transfers;

/// <summary>
/// Where uploaded gcode lives and what is allowed in, bound from the <c>FileStorage</c>
/// configuration section.
/// </summary>
public class FileStorageOptions
{
    public const string SectionName = "FileStorage";

    /// <summary>
    /// Directory for uploaded files. Relative paths resolve against the content root.
    /// </summary>
    /// <remarks>
    /// Defaults under <c>data/</c> because that is what <c>compose.yaml</c> mounts as a volume for
    /// the SQLite file - uploads that landed anywhere else would vanish when the container is
    /// replaced, which is the same trap the database already avoids. The warning about network
    /// filesystems in AGENT-NOTES §5 applies here too, for a different reason: reads happen on the
    /// connection actor's loop, bounded only by its send timeout.
    /// </remarks>
    public string Directory { get; set; } = "data/files";

    /// <summary>
    /// Largest upload accepted, in bytes. Default 512 MiB.
    /// </summary>
    /// <remarks>
    /// The hard ceiling is firmware's, not ours: <c>orig_size</c> is <c>uint32</c> on the wire
    /// (command.hpp:64-65, whose comment notes FatFS cannot exceed 4 GB either), so anything at or
    /// above 4 GiB cannot be described to a printer at all. The default is far below that because a
    /// sliced model reaching hundreds of megabytes is already unusual, and an unbounded upload
    /// endpoint on an internet-facing deployment is a disk-exhaustion primitive
    /// (notes/internet-exposure.md).
    /// </remarks>
    public long MaxUploadBytes { get; set; } = 512L * 1024 * 1024;
}
