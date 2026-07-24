namespace PrinterService.Host.PrusaConnect.Commands;

public class SetPrinterIdle : ISendableCommand
{
    // Not SET_PRINTER_IDLE - the firmware's wire string is SET_IDLE (command.cpp:166).
    public string WireName => "SET_IDLE";
}
