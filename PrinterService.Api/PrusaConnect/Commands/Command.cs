namespace PrinterService.Api.PrusaConnect.Commands;

public class Command
{
    public uint CommandId { get; set; }
    
    public required ICommand CommandData { get; set; }
}
