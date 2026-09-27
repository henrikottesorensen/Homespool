namespace Homespool.FakePrinter;

/// <summary>One thing on the fake printer's drive.</summary>
/// <param name="Path">Full path, always under <see cref="FakeStorage.Root"/>.</param>
/// <param name="Size">Bytes. Zero for a folder, which firmware renders without a size at all.</param>
/// <param name="Modified">Unix seconds. Zero for a folder, for the same reason.</param>
/// <param name="IsFolder">
/// Whether this renders as <c>FOLDER</c> or as <c>PRINT_FILE</c>. Real firmware has a third value -
/// a captured listing reports <c>prusa_printer_settings.ini</c> as plain <c>FILE</c> - which this
/// does not model, because nothing on a fake drive is a non-printable file unless a test puts one
/// there.
/// </param>
public sealed record FakeStorageEntry(string Path, long Size, long Modified, bool IsFolder)
{
    /// <summary>
    /// The file's <c>objects_info</c> header, verbatim - the slicer's JSON inside a string - or null
    /// for a file sliced without cancel-object labels. Rendered into its <c>FILE_INFO</c> the way
    /// firmware relays any header it does not read.
    /// </summary>
    public string? ObjectsInfo { get; init; }

    /// <summary>
    /// The file's <c>bed_shape</c> header, or null. Only a plain-gcode file carries one on hardware.
    /// </summary>
    public string? BedShape { get; init; }
}
