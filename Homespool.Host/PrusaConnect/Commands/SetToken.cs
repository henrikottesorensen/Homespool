namespace Homespool.Host.PrusaConnect.Commands;

public class SetToken : ICommand
{
    public required byte[] Token { get; set; }
}
