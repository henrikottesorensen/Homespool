namespace PrinterService.Host.PrusaConnect.Commands;

public class SendFileInfo : ICommand
{
    public required string Path { get; set; }
}
