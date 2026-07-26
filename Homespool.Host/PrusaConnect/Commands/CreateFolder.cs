namespace Homespool.Host.PrusaConnect.Commands;

public class CreateFolder : ICommand
{
    public required string Path { get; set; }
}
