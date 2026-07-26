using System;

namespace PrinterService.Host.PrusaConnect.DTO;

public class CodeResponseDTO
{
    public required string TemporaryCode { get; set; }

    public DateTimeOffset Expires { get; set; }
}
