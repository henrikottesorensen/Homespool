namespace Homespool.Host.PrusaConnect.Commands;

public class DeleteFile : ICommand
{
    public required string Path { get; set; }
}
