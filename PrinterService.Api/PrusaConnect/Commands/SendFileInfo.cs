namespace PrinterService.Api.PrusaConnect.Commands;

public class SendFileInfo : ICommand
{
    public required string Path { get; set; }
}
