using System.Text.Json.Serialization;

namespace Homespool.Host.PrusaConnect.DTO.EventMessages;

/// <summary>One entry in a directory listing.</summary>
/// <remarks>
/// The short/long name pair is the point of this shape: <see cref="Name"/> is the FAT short name
/// (<c>d_name</c>) and <see cref="DisplayName"/> the long one (<c>dirent_lfn</c>), both straight
/// from the printer's filesystem.
/// </remarks>
public class FileInfoChildDTO
{
    /// <summary>The 8.3 short name, e.g. <c>MODEL~1.GCO</c>.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>The long name, e.g. <c>model.gcode</c>.</summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("m_timestamp")]
    public long? ModifiedTimestamp { get; set; }

    /// <summary>
    /// True for an entry that cannot be written - including, notably, a transfer still in progress,
    /// which is reported as a read-only regular file rather than as the directory it really is.
    /// </summary>
    [JsonPropertyName("read_only")]
    public bool? ReadOnly { get; set; }

    /// <summary><c>PRINT_FILE</c>, <c>FOLDER</c>, or another of firmware's own labels. See
    /// <see cref="FileInfoEventDataDTO.Type"/> for why this is a string.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}
