namespace PrinterService.Api.PrusaConnect.Commands;

public class DeleteFile : ICommand
{
    public string Path { set; get; }
}
