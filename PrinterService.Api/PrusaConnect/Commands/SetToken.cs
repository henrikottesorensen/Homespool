namespace PrinterService.Api.PrusaConnect.Commands;

public class SetToken : ICommand
{
    public required byte[] Token { set; get; }
}
