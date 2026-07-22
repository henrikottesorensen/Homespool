namespace PrinterService.Api.PrusaConnect.Commands;

public class Broken : ICommand
{
    public string Reason { get; set; }
}

