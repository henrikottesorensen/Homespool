using System.Text.Json.Serialization;

namespace PrinterService.Host.PrusaConnect.DTO.Telemetry;

public class EnclosureTelemetryDTO
{
    [JsonPropertyName("temp")]
    public float Temperature { get; set; }
    
    [JsonPropertyName("fan_1_rpm")]
    public int FanSpeed { get; set; }
    
    [JsonPropertyName("time_in_use")]
    public int TimeIsUse { get; set; }
}
