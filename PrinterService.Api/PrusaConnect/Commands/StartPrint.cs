namespace PrinterService.Api.PrusaConnect.Commands;

public class StartPrint : ICommand
{
    public string Path { get; set; }
    
    public ToolMapping Tool { get; set; }
}
