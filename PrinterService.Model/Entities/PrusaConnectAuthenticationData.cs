using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrinterService.Model.Entities;

public class PrusaConnectAuthenticationData
{
    public long Id { get; set; }
    
    public int? PrinterId { get; set; }
    
    /// <summary>
    /// The claimed printer, or null while the registration is still unclaimed.
    /// </summary>
    /// <remarks>
    /// Nullable to match <see cref="PrinterId"/>: a registration exists from the moment the printer
    /// POSTs to /p/register, but no printer row is created until a user claims the code. Declaring it
    /// non-nullable made <c>auth.Printer.Owner</c> in the authentication handler look safe when it is
    /// not - AGENT-NOTES §3 recorded that as a latent NPE. Now it is a compile error instead.
    /// </remarks>
    [ForeignKey(nameof(PrinterId))]
    public virtual Printer? Printer { get; set; }
    
    public required string SerialNumber { get; set; }
    
    public required string FingerPrint { get; set; }
    
    public string? HashedToken { get; set; }
    
    public required string TemporaryCode { get; set; }
    
    public DateTimeOffset TemporaryCodeExpiry { get; set; }
    
    public DateTimeOffset CreatedAt { get; set; }
    
    public DateTimeOffset? TokenCreatedAt { get; set; }
}
