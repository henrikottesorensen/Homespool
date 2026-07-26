namespace PrinterService.Host.PrusaConnect.Commands;

public class SetValue : ICommand
{
    public PropertyName Name { get; set; }

    // For names that relate to stuff we have more of… like nozzles.
    public int Index { get; set; }

    public required object Value { get; set; }
}
