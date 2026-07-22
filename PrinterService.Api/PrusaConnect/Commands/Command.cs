namespace PrinterService.Api.PrusaConnect.Commands;

public class Command
{
    public uint CommandId { get; set; }
    
    public ICommand CommandData { get; set; }
}
