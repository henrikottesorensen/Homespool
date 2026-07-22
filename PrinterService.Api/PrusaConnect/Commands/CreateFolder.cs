namespace PrinterService.Api.PrusaConnect.Commands;

public class CreateFolder : ICommand
{
    public required string Path { set; get; }
}
