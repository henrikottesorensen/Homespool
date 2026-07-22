namespace PrinterService.Api.PrusaConnect.Commands;

public class DialogAction : ICommand
{
    // 31 bit value
    public int DialogId { set; get; }
    
    public DialogResponse Response { set; get; }
}
