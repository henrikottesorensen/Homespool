using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrinterService.Model.Entities;

public class PrusaConnectAuthenticationData
{
    public long Id { get; set; }
    
    public int? PrinterId { get; set; }
    
    [ForeignKey(nameof(PrinterId))]
    public virtual Printer Printer { get; set; }
    
    public string SerialNumber { get; set; }
    
    public string FingerPrint { get; set; }
    
    public string? HashedToken { get; set; }
    
    public string TemporaryCode { get; set; }
    
    public DateTimeOffset TemporaryCodeExpiry { get; set; }
    
    public DateTimeOffset CreatedAt { get; set; }
    
    public DateTimeOffset? TokenCreatedAt { get; set; }
}
