namespace Homespool.Host.PrusaConnect.Commands;

public class DeleteFolder : ICommand
{
    public required string Path { get; set; }
}
