using System;

namespace PrinterService.Api.PrusaConnect.DTO;

public class CodeResponseDTO
{
    public required string TemporaryCode { get; set; }
    
    public DateTimeOffset Date { get; set; }
    
    public DateTimeOffset Expires { get; set; }
}
