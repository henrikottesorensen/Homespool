using System.Text.Json.Serialization;

namespace PrinterService.Host.PrusaConnect.DTO.Telemetry;

public class ChamberTelemetryDTO
{
    [JsonPropertyName("temp")]
    public float Temperature { get; set; }
    
    [JsonPropertyName("target_temp")]
    public int TargetTemperture { get; set; }
    
    [JsonPropertyName("fan_1_rpm")]
    public int Fan1Speed { get; set; }
    
    [JsonPropertyName("fan_2_rpm")]
    public int Fan2Speed { get; set; }
    
    [JsonPropertyName("fan_pwm_target")]
    public int FanPwmTarget { get; set; }
}
