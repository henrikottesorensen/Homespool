using System;

using Homespool.Host.PrusaConnect.DTO.EventMessages;

namespace Homespool.Host.DTO;

/// <summary>One entry in a printer's storage listing, as this API reports it.</summary>
/// <remarks>
/// Translated out of firmware's vocabulary rather than passed through: <c>/api/v1</c> is ours, and
/// only <c>/p/*</c> owes Prusa anything. The
/// translation earns its keep on two fields in particular - a Unix timestamp becomes a real instant,
/// and the wire's <c>name</c>/<c>display_name</c> pair becomes
/// <see cref="ShortName"/>/<see cref="Name"/>, which says which of the two is which.
/// </remarks>
public class PrinterStorageEntryDTO
{
    /// <summary>The long name, as a person wrote it - e.g. <c>Barrel 2_0.25n_0.07mm_PLA.bgcode</c>.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// The printer's 8.3 alias - e.g. <c>BARREL~4.BGC</c>. Nearly always present and nearly always
    /// different from <see cref="Name"/>: of 69 entries in a captured listing, every one was aliased.
    /// </summary>
    public string? ShortName { get; set; }

    /// <summary>Size in bytes.</summary>
    public long? Size { get; set; }

    /// <summary>Last modified, converted from firmware's Unix seconds.</summary>
    public DateTimeOffset? ModifiedAt { get; set; }

    /// <summary>
    /// True for an entry that cannot be written - which notably includes a transfer still arriving,
    /// reported as a read-only regular file because it is printable before it is complete.
    /// </summary>
    public bool? ReadOnly { get; set; }

    /// <summary>
    /// <c>printFile</c>, <c>folder</c>, <c>file</c>, or whatever else firmware names - see
    /// <see cref="PrinterStorageReadDTO.Kind"/>.
    /// </summary>
    public string? Kind { get; set; }

    public static PrinterStorageEntryDTO FromChild(FileInfoChildDTO child)
    {
        return new()
        {
            Name = child.DisplayName,
            ShortName = child.Name,
            Size = child.Size,
            ModifiedAt = PrinterStorageReadDTO.ToInstant(child.ModifiedTimestamp),
            ReadOnly = child.ReadOnly,
            Kind = PrinterStorageReadDTO.ToKind(child.Type),
        };
    }
}
