using PrinterService.Model;

namespace PrinterService.Host.PrusaConnect.DTO.App;

/// <summary>
/// Maps <see cref="PrinterStatus"/> to Connect's <c>Printer-read.state</c> vocabulary. A mapping
/// rather than a shared enum, per AGENT-NOTES phase-1.5 §15: the two vocabularies happen to carry
/// the same twelve values today, but Connect's is the wire contract and ours is free to diverge.
/// </summary>
public static class PrinterStatusExtensions
{
    public static string ToConnectState(this PrinterStatus status) => status switch
    {
        PrinterStatus.Idle => "IDLE",
        PrinterStatus.Ready => "READY",
        PrinterStatus.Printing => "PRINTING",
        PrinterStatus.Paused => "PAUSED",
        PrinterStatus.Attention => "ATTENTION",
        PrinterStatus.Stopped => "STOPPED",
        PrinterStatus.Finished => "FINISHED",
        PrinterStatus.Busy => "BUSY",
        PrinterStatus.Error => "ERROR",
        PrinterStatus.Manipulating => "MANIPULATING",
        PrinterStatus.Offline => "OFFLINE",
        _ => "UNKNOWN",
    };
}
