namespace PrinterService.Host.PrusaConnect.Commands;

public class PausePrint : ISendableCommand
{
    public string WireName => "PAUSE_PRINT";
}
