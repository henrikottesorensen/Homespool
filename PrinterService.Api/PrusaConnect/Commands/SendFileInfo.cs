namespace PrinterService.Api.PrusaConnect.Commands;

public class SendFileInfo : ICommand
{
    public string Path { get; set; }
}
