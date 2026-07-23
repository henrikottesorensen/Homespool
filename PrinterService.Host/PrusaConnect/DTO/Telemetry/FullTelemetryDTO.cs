using System.Text.Json.Serialization;

namespace PrinterService.Host.PrusaConnect.DTO.Telemetry;

public class FullTelemetryDTO
{
    [JsonPropertyName("temp_nozzle")]
    public float NozzleTemperature { get; set; }
    
    [JsonPropertyName("temp_bed")]
    public float BedTemperature { get; set; }
    
    [JsonPropertyName("target_nozzle")]
    public float TargetNozzleTemperature { get; set; }
    
    [JsonPropertyName("target_bed")]
    public float TargetBedTemperature { get; set; }
    
    [JsonPropertyName("speed")]
    public int Speed { get; set; } 
    
    [JsonPropertyName("flow")]
    public int Flow { get; set; } 
    
    [JsonPropertyName("material")]
    public string? Material { get; set; }
    
    [JsonPropertyName("axis_x")]
    public float? XAxis { get; set; }
    
    [JsonPropertyName("axis_y")]
    public float? YAxis { get; set; }
    
    [JsonPropertyName("axis_z")]
    public float ZAxis { get; set; }
    
    [JsonPropertyName("enclosure")]
    public EnclosureTelemetryDTO? Enclosure { get; set; }
    
    [JsonPropertyName("chamber")]
    public ChamberTelemetryDTO? Chamber { get; set; }
    
    [JsonPropertyName("command_id")]
    public string? CommandId { get; set; }
    
    [JsonPropertyName("dialog_id")]
    public uint? DialogId { get; set; }
    
    [JsonPropertyName("state")]
    public required string Status { get; set; }
}
