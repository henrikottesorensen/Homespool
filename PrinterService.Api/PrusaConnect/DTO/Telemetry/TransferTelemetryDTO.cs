using System.Text.Json.Serialization;

namespace PrinterService.Api.PrusaConnect.DTO.Telemetry;

public class TransferTelemetryDTO
{
    [JsonPropertyName("transfer_id")]
    public int TransferId { get; set; }
    
    [JsonPropertyName("transfer_transferred")]
    public int TransferTransferred { get; set; }
    
    [JsonPropertyName("transfer_time_remaining")]
    public int TransferTimeRemaining { get; set; }
    
    [JsonPropertyName("transfer_progress")]
    public double TransferProgress { get; set; }
    
    
}
