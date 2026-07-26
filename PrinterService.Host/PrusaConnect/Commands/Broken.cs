namespace PrinterService.Host.PrusaConnect.Commands;

public class Broken : ICommand
{
    public required string Reason { get; set; }
}
