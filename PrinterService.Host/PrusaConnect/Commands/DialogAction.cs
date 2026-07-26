namespace PrinterService.Host.PrusaConnect.Commands;

public class DialogAction : ICommand
{
    // 31 bit value
    public int DialogId { get; set; }

    public DialogResponse Response { get; set; }
}
