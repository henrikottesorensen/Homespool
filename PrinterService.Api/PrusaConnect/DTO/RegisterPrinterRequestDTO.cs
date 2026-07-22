using System.Text.Json.Serialization;

namespace PrinterService.Api.PrusaConnect.DTO;

public class RegisterPrinterRequestDTO
{
    [JsonPropertyName("sn")]
    public required string SerialNumber { get; set; }
    
    [JsonPropertyName("fingerprint")]
    public required string FingerPrint { get; set; }
    
    [JsonPropertyName("printer_type")]
    public required string PrinterType { get; set; }
    
    [JsonPropertyName("firmware")]
    public required string Firmware { get; set; }
}
