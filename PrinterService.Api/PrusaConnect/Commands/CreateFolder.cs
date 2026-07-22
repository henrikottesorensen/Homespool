namespace PrinterService.Api.PrusaConnect.Commands;

public class CreateFolder : ICommand
{
    public string Path { set; get; }
}
