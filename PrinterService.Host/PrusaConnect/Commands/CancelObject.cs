namespace PrinterService.Host.PrusaConnect.Commands;

public class CancelObject : ICommand
{
    public ushort Id { get; set; }
}
