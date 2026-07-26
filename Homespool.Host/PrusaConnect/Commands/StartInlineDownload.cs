namespace Homespool.Host.PrusaConnect.Commands;

public class StartInlineDownload : ICommand
{
    public ulong TeamId { get; set; }

    public long OriginalSize { get; set; }

    public required string Path { get; set; }

    public byte[] Hash { get; set; } = new byte[29];
}
