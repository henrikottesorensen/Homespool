namespace PrinterService.Api.PrusaConnect.Commands;

public class StartPrint : ICommand
{
    public required string Path { get; set; }
    
    public required ToolMapping Tool { get; set; }
}
