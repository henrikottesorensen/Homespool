namespace Homespool.Host.PrusaConnect.Commands;

public class Command
{
    public uint CommandId { get; set; }

    public required ICommand CommandData { get; set; }
}
