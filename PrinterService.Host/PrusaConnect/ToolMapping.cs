namespace PrinterService.Host.PrusaConnect;

#pragma warning disable CA1814

public class ToolMapping
{
    public byte[,] Mapping { get; set; } = new byte[1, 1];
}

#pragma warning restore CA1814
