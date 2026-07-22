namespace PrinterService.Api.PrusaConnect.Commands;

public class StartInlineDownload : ICommand
{
    public ulong TeamId { set; get; }
    
    public long OriginalSize { set; get; }
    
    public string Path { set; get; }
    
    public byte[] Hash { set; get; } = new byte[29];
}
