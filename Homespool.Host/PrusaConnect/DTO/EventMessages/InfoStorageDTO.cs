using System.Text.Json.Serialization;

namespace Homespool.Host.PrusaConnect.DTO.EventMessages;

public class InfoStorageDTO
{
    [JsonPropertyName("mountpoint")]
    public string? MountPoint { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("read_only")]
    public bool ReadOnly { get; set; }

    [JsonPropertyName("free_space")]
    public long FreeSpace { get; set; }

    [JsonPropertyName("is_sfn")]
    public bool IsSfn { get; set; }
}
