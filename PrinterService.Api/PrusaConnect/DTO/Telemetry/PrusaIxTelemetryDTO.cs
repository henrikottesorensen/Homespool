using System.Text.Json.Serialization;

namespace PrinterService.Api.PrusaConnect.DTO.Telemetry;

public class PrusaIxTelemetryDTO
{
    [JsonPropertyName("temp_heatbreak")]
    public float HeatbreakTemperature { get; set; }
    
    [JsonPropertyName("temp_psu")]
    public float PsuTemperature { get; set; }

    [JsonPropertyName("temp_ambient")]
    public float AmbientTemperature { get; set; }

    [JsonPropertyName("extruder_fs_state")]
    public string ExtruderFilamentSesnorStatus { get; set; }
    
    [JsonPropertyName("remote_fs_state")]
    public string RmmoteFilamentSesnorStatus { get; set; }
}
