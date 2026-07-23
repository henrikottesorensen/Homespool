using System.Text.Json.Serialization;

namespace PrinterService.Host.PrusaConnect.DTO.Telemetry;

public class JobTelemetryDTO
{
    [JsonPropertyName("job_id")]
    public int JobId { get; set; }
    
    [JsonPropertyName("time_printing")]
    public int TimePrinting { get; set; }
    
    [JsonPropertyName("time_remaining")]
    public int TimeRemaining { get; set; }
    
    [JsonPropertyName("filament_change_in")]
    public int TimeToFilamentChange { get; set; }
    
    [JsonPropertyName("progress")]
    public int Progress { get; set; }
    
    [JsonPropertyName("fan_extruder")]
    public int ExtruderFan { get; set; }
    
    [JsonPropertyName("fan_print")]
    public int PrintFan { get; set; }
    
    [JsonPropertyName("filament")]
    public float FilamentUsed { get; set; }
}
