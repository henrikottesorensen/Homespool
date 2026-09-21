using System;

using Homespool.Host.PrintFiles;
using Homespool.Model.Entities;

namespace Homespool.Host.DTO;

/// <summary>One of the caller's files, as the API reports it.</summary>
/// <remarks>
/// <b>No path on a printer.</b> Where a file sits is a fact about each printer that received it, as
/// that printer reported it - a long name can come back as <c>LAMPEN~2.BGC</c> - and a rename here
/// does not move the copy there. A path computed from this file's name would read as one and be
/// neither; <c>…/printers/{uuid}/storage/usb/</c> lists what a printer actually holds.
/// </remarks>
public class PrintFileReadDTO
{
    /// <summary>The name the user gave it, which is also its identity here.</summary>
    public required string Name { get; set; }

    /// <summary>Size in bytes.</summary>
    public required long Size { get; set; }

    /// <summary>When it was last written - for an overwrite, when it was replaced.</summary>
    public required DateTimeOffset UploadedAt { get; set; }

    /// <summary>
    /// SHA-384 of the content, base64url. Null for a file stored before digests were taken, which are
    /// deliberately not hashed after the fact.
    /// </summary>
    public string? Digest { get; set; }

    /// <summary>What the file says it was sliced for, and how much of that could be read.</summary>
    public required PrintFileMetadataReadDTO Metadata { get; set; }

    /// <summary>A file and what is recorded about it.</summary>
    /// <param name="file">The bytes, as the store holds them.</param>
    /// <param name="row">What is recorded about them, or null when the file has not been indexed.</param>
    public static PrintFileReadDTO From(StoredFile file, PrintFile? row)
    {
        ArgumentNullException.ThrowIfNull(file);

        return new()
        {
            Name = file.FileName,
            Size = file.Length,
            UploadedAt = file.UploadedAt,
            Digest = row?.Digest,
            Metadata = PrintFileMetadataReadDTO.From(row),
        };
    }

    /// <summary>A listed file and its row.</summary>
    public static PrintFileReadDTO From(CataloguedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return From(file.File, file.Row);
    }
}
