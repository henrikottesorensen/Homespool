namespace PrinterService.Api.PrusaConnect.Commands;

public class DeleteFolder : ICommand
{
    public required string Path { set; get; }
}
