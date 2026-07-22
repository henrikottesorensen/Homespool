namespace PrinterService.Api.PrusaConnect.Commands;

public class SendJobInfo : ICommand
{
    public ushort JobId { get; set; }
}
