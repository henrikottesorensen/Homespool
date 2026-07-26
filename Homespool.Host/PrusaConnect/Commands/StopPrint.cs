namespace Homespool.Host.PrusaConnect.Commands;

public class StopPrint : ISendableCommand
{
    public string WireName => "STOP_PRINT";
}
