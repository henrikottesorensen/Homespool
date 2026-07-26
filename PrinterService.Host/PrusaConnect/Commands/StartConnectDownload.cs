namespace PrinterService.Host.PrusaConnect.Commands;

public class StartConnectDownload : ICommand
{
    public required string Path { get; set; }

    public ushort? Port { get; set; }

    public long OriginalSize { get; set; }
}
