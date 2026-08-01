using System;

using Homespool.Host.PrintFiles;

namespace Homespool.Host.DTO;

/// <summary>One of the caller's files, as the API reports it.</summary>
public class PrintFileReadDTO
{
    /// <summary>The name the user gave it, which is also its identity here.</summary>
    public required string Name { get; set; }

    /// <summary>Size in bytes.</summary>
    public required long Size { get; set; }

    /// <summary>When it was last written - for an overwrite, when it was replaced.</summary>
    public required DateTimeOffset UploadedAt { get; set; }

    /// <summary>
    /// Where it lands on a printer once sent, and therefore what a print call needs afterwards.
    /// </summary>
    public required string PrinterPath { get; set; }

    public static PrintFileReadDTO FromStored(StoredFile file) => new()
    {
        Name = file.FileName,
        Size = file.Length,
        UploadedAt = file.UploadedAt,
        PrinterPath = file.PrinterPath,
    };
}
