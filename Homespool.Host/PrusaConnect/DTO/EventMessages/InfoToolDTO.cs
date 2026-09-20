using System.Text.Json.Serialization;

namespace Homespool.Host.PrusaConnect.DTO.EventMessages;

/// <summary><c>material</c> is the literal string <c>"---"</c> when no filament is set - a
/// firmware sentinel value, not something this DTO interprets.</summary>
public class InfoToolDTO
{
    /// <summary>
    /// Nullable because JSON's only way to write a float that is not a number is <c>null</c>, and a
    /// <c>null</c> into a plain <c>float</c> throws - here taking the whole <c>INFO</c> identity with it.
    /// </summary>
    [JsonPropertyName("nozzle_diameter")]
    public float? NozzleDiameter { get; set; }

    [JsonPropertyName("high_flow")]
    public bool HighFlow { get; set; }

    [JsonPropertyName("hardened")]
    public bool Hardened { get; set; }

    [JsonPropertyName("material")]
    public string? Material { get; set; }
}
