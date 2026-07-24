namespace PrinterService.Host.PrusaConnect.Commands;

public class SetPrinterReady : ISendableCommand
{
    public string WireName => "SET_PRINTER_READY";
}
