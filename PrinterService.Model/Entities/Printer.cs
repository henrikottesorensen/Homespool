using System;
using System.ComponentModel.DataAnnotations;

using Microsoft.EntityFrameworkCore;

namespace PrinterService.Model;

public class Printer
{
    [Key]
    public Guid Uuid { get; set; }
    
    public PrinterType Type { get; set; }
    
    public long Owner { get; set; }
    
    public required string Name { get; set; }
    
    public required string Model { get; set; }
    
    public string Location { get; set; }
    
    public string Firmware { get; set; }
    
    public string Material { get; set; }
    
    public PrinterStatus Status { get; set; }
    
    public string? LoadedMaterial { get; set; }
    
    public DateTimeOffset CreatedAt { get; set; }
    
    public DateTimeOffset UpdatedAt { get; set; }
}
