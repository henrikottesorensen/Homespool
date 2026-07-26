namespace Homespool.Host.PrusaConnect.Commands;

public class StartEncryptedDownload : ICommand
{
    public required string Path { get; set; }

    public ushort? Port { get; set; }

    public byte[] Key { get; set; } = new byte[16];

    public byte[] Iv { get; set; } = new byte[16];

    public long OriginalSize { get; set; }
}
