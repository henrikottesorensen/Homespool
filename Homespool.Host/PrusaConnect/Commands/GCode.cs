namespace Homespool.Host.PrusaConnect.Commands;

public class GCode : ICommand
{
    public required byte[] GCodeData { get; set; }

    public uint Size { get; set; }
}
