using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrinterService.Host.PrusaConnect.DTO.Telemetry;

/// <summary>
/// The telemetry <c>"slot"</c> object - only sent when the printer has more than one tool. Mixes
/// numbered per-slot sub-objects ("1".."N", 1-based) with sibling fixed keys, so the numbered
/// entries land in <see cref="Slots"/> via <see cref="JsonExtensionDataAttribute"/> rather than as
/// named properties; deserialize each value into <see cref="ToolTelemetryDTO"/>.
/// </summary>
public class SlotsTelemetryDTO
{
    [JsonPropertyName("active")]
    public int Active { get; set; }

    /// <summary>MMU builds only.</summary>
    [JsonPropertyName("state")]
    public int? MmuState { get; set; }

    /// <summary>MMU builds only, a single character.</summary>
    [JsonPropertyName("command")]
    public string? MmuCommand { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Slots { get; set; }
}

/// <summary>One numbered entry inside <see cref="SlotsTelemetryDTO.Slots"/>. <c>fan_hotend</c>/
/// <c>fan_print</c> are floats on the wire despite being <c>uint16_t</c> in firmware.</summary>
public class ToolTelemetryDTO
{
    [JsonPropertyName("material")]
    public string? Material { get; set; }

    [JsonPropertyName("temp")]
    public float Temperature { get; set; }

    [JsonPropertyName("fan_hotend")]
    public float HotendFan { get; set; }

    [JsonPropertyName("fan_print")]
    public float PrintFan { get; set; }
}
