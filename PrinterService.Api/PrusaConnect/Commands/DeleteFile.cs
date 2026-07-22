namespace PrinterService.Api.PrusaConnect.Commands;

public class DeleteFile : ICommand
{
    public required string Path { set; get; }
}
