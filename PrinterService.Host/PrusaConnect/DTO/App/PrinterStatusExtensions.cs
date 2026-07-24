using System;

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

    /// <summary>
    /// Parses the <c>state</c> field firmware/the SDK actually send on <c>TelemetryDTO</c>/
    /// <c>EventDTO</c>. Deliberately narrower than <c>Enum.Parse&lt;PrinterStatus&gt;</c> against
    /// every <see cref="PrinterStatus"/> member: <c>UNKNOWN</c>, <c>MANIPULATING</c> and
    /// <c>OFFLINE</c> are server-synthesised connectivity/aggregate states (AGENT-NOTES
    /// phase-1.5 §15) that Buddy never puts on the wire, so accepting them here would let a
    /// wire value silently pass as one even though it isn't part of the real 9-value vocabulary
    /// (<c>Prusa-Connect-SDK-Printer/prusa/connect/printer/const.py</c> <c>State</c>).
    /// </summary>
    public static PrinterStatus ParseWireState(string wireValue) => wireValue switch
    {
        "IDLE" => PrinterStatus.Idle,
        "BUSY" => PrinterStatus.Busy,
        "PRINTING" => PrinterStatus.Printing,
        "PAUSED" => PrinterStatus.Paused,
        "FINISHED" => PrinterStatus.Finished,
        "STOPPED" => PrinterStatus.Stopped,
        "ERROR" => PrinterStatus.Error,
        "ATTENTION" => PrinterStatus.Attention,
        "READY" => PrinterStatus.Ready,
        _ => throw new ArgumentOutOfRangeException(nameof(wireValue), wireValue,
                                                    "Not one of the 9 printer states firmware sends."),
    };
}
