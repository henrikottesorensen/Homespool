namespace Homespool.Host.Firmware;

/// <summary>Where firmware images are kept, and how large one may be.</summary>
public class FirmwareStorageOptions
{
    public const string SectionName = "Firmware";

    /// <summary>
    /// The largest image accepted, in bytes. A constant rather than configuration, because the upload
    /// page's request cap is an attribute argument and has to be one.
    /// </summary>
    /// <remarks>
    /// Prusa's images run to a few megabytes - the Core One's 7.0.0 is 3.3 MB - so 32 MiB is room to
    /// grow, and small enough that an upload which is not an image is refused before it fills a disk.
    /// </remarks>
    public const long MaxImageBytes = 32 * 1024 * 1024;

    /// <summary>
    /// The directory images are kept in, relative to the content root unless rooted. Never inside the
    /// print-file store: nothing that walks users' files may come across an image.
    /// </summary>
    public string Directory { get; set; } = "data/firmware";
}
