namespace PrinterService.Host.PrusaConnect.Commands;

public class SetToken : ICommand
{
    public required byte[] Token { set; get; }
}
