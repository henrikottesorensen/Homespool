using System.Text.Json.Serialization;

namespace Homespool.Host.PrusaConnect.DTO.EventMessages;

public class InfoMmuDTO
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}
