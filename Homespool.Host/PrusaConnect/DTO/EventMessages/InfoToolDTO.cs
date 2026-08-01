using System.Text.Json.Serialization;

namespace Homespool.Host.PrusaConnect.DTO.EventMessages;

/// <summary><c>material</c> is the literal string <c>"---"</c> when no filament is set - a
/// firmware sentinel value, not something this DTO interprets.</summary>
public class InfoToolDTO
{
    [JsonPropertyName("nozzle_diameter")]
    public float NozzleDiameter { get; set; }

    [JsonPropertyName("high_flow")]
    public bool HighFlow { get; set; }

    [JsonPropertyName("hardened")]
    public bool Hardened { get; set; }

    [JsonPropertyName("material")]
    public string? Material { get; set; }
}
