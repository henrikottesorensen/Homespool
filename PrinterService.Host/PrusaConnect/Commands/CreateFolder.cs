namespace PrinterService.Host.PrusaConnect.Commands;

public class CreateFolder : ICommand
{
    public required string Path { set; get; }
}
