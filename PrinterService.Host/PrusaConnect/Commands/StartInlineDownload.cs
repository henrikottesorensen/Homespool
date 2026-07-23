namespace PrinterService.Host.PrusaConnect.Commands;

public class StartInlineDownload : ICommand
{
    public ulong TeamId { set; get; }
    
    public long OriginalSize { set; get; }
    
    public required string Path { set; get; }
    
    public byte[] Hash { set; get; } = new byte[29];
}
