namespace PrinterService.Api.PrusaConnect.Commands;

public class StartEncryptedDownload : ICommand
{
    public string Path { set; get; }
    
    public ushort? Port { set; get; }

    public byte[] Key { set; get; } = new byte[16];
    
    public byte[] Iv  { set; get; } = new byte[16];
    
    public long OriginalSize { set; get; }
}
