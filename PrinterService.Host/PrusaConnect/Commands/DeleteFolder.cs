namespace PrinterService.Host.PrusaConnect.Commands;

public class DeleteFolder : ICommand
{
    public required string Path { set; get; }
}
