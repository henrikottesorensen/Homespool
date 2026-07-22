using System.Text.Json.Serialization;

namespace PrinterService.Api.PrusaConnect.DTO.Telemetry;

public class ToolsTelemetryDTO
{
    // Why isn't this a JSON array?
    [JsonPropertyName("1")]
    public ToolTelemetryDTO? Tool1 { get; set; }

    [JsonPropertyName("2")]
    public ToolTelemetryDTO? Tool2 { get; set; }

    [JsonPropertyName("3")]
    public ToolTelemetryDTO? Tool3 { get; set; }

    [JsonPropertyName("4")]
    public ToolTelemetryDTO? Tool4 { get; set; }

    [JsonPropertyName("5")]
    public ToolTelemetryDTO? Tool5 { get; set; }

    [JsonPropertyName("6")]
    public ToolTelemetryDTO? Tool6 { get; set; }

    [JsonPropertyName("7")]
    public ToolTelemetryDTO? Tool7 { get; set; }

    [JsonPropertyName("8")]
    public ToolTelemetryDTO? Tool8 { get; set; }

    [JsonPropertyName("9")]
    public ToolTelemetryDTO? Tool9 { get; set; }

    [JsonPropertyName("10")]
    public ToolTelemetryDTO? Tool10 { get; set; }
    
    [JsonPropertyName("state")]
    public int? Status { get; set; }
    
    [JsonPropertyName("command")]
    public string? Command { get; set; }
    
    [JsonPropertyName("active")]
    public int ActiveTool  { get; set; }
}

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
