namespace PrinterService.Api.PrusaConnect.Commands;

public class SetToken : ICommand
{
    public byte[] Token { set; get; }
}
