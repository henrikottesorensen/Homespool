namespace PrinterService.Api.PrusaConnect.Commands;

public class DeleteFolder : ICommand
{
    public string Path { set; get; }
}
