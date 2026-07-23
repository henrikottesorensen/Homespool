namespace PrinterService.Host.PrusaConnect.Commands;

public class DeleteFile : ICommand
{
    public required string Path { set; get; }
}
