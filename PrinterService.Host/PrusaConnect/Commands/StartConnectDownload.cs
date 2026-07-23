namespace PrinterService.Host.PrusaConnect.Commands;

public class StartConnectDownload : ICommand
{
    public required string Path { set; get; }
    
    public ushort? Port { set; get; }
    
    public long OriginalSize { set; get; }
}
