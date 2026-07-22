namespace PrinterService.Api.PrusaConnect.Commands;

public class GCode : ICommand
{
    public byte[] GCodeData { get; set; }
    
    public uint Size { get; set; }
}
