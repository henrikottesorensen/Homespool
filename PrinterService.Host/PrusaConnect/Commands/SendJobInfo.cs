namespace PrinterService.Host.PrusaConnect.Commands;

public class SendJobInfo : ICommand
{
    public ushort JobId { get; set; }
}
