namespace PrinterService.Host.PrusaConnect.Commands;

public class CancelPrinterReady : ISendableCommand
{
    public string WireName => "CANCEL_PRINTER_READY";
}
