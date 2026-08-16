using System;

using Homespool.Model;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// The Prusa Connect event vocabulary, mapped to and from <see cref="PrinterEventType"/> — the one
/// place the wire's words and the domain's values meet. Deliberately a written-out table rather
/// than a casing transform: the two vocabularies are separate things that happen to correspond,
/// and <c>MEDIUM_INSERTED</c> ↔ <see cref="PrinterEventType.StorageInserted"/> is the pair that
/// proves a mechanical rule would be a coincidence, not a contract.
/// </summary>
/// <remarks>
/// <para>
/// The wire words are the Connect SDK's <c>Event</c> enum
/// (<c>Prusa-Connect-SDK-Printer/prusa/connect/printer/const.py</c> at the pinned ref), which
/// Buddy firmware shares. Not every word can come from every client: <c>MEDIUM_EJECTED</c>,
/// <c>MEDIUM_INSERTED</c>, <c>MESH_BED_DATA</c> and <c>SLOT_EVENT</c> are in the SDK's enum but
/// absent from Buddy firmware's own <c>EventType</c> (<c>planner.hpp</c>), while
/// <c>CANCELABLE_CHANGED</c> is the reverse — a firmware-side addition the SDK has not caught up
/// to.
/// </para>
/// <para>
/// <b>The table is bijective, and <see cref="Telemetry.TelemetryWriter"/> depends on that</b>: it stores
/// <see cref="Homespool.Model.Entities.PrinterEvent.WireType"/> by formatting the parsed value
/// back through <see cref="Format"/>, which reproduces the received word byte-for-byte precisely
/// because each value maps to exactly one word. An unknown word throws in <see cref="Parse"/> —
/// the same posture as <c>ParseWireState</c>, per <c>notes/protocol-vocabulary-boundary.md</c>:
/// the wire vocabulary demonstrably does not grow, so a silent unknown would be worse than a loud
/// one — and therefore never reaches persistence at all.
/// </para>
/// </remarks>
public static class PrusaEventWireMapping
{
    /// <summary>
    /// Parses a wire event word. Throws on anything outside the known vocabulary rather than
    /// guessing — see the class remarks for why loud beats silent here.
    /// </summary>
    public static PrinterEventType Parse(string wireValue)
    {
        return wireValue switch
        {
            "ACCEPTED" => PrinterEventType.Accepted,
            "REJECTED" => PrinterEventType.Rejected,
            "FAILED" => PrinterEventType.Failed,
            "FINISHED" => PrinterEventType.Finished,
            "INFO" => PrinterEventType.Info,
            "STATE_CHANGED" => PrinterEventType.StateChanged,
            "MEDIUM_EJECTED" => PrinterEventType.StorageEjected,
            "MEDIUM_INSERTED" => PrinterEventType.StorageInserted,
            "FILE_CHANGED" => PrinterEventType.FileChanged,
            "FILE_INFO" => PrinterEventType.FileInfo,
            "JOB_INFO" => PrinterEventType.JobInfo,
            "TRANSFER_INFO" => PrinterEventType.TransferInfo,
            "MESH_BED_DATA" => PrinterEventType.MeshBedData,
            "TRANSFER_ABORTED" => PrinterEventType.TransferAborted,
            "TRANSFER_STOPPED" => PrinterEventType.TransferStopped,
            "TRANSFER_FINISHED" => PrinterEventType.TransferFinished,
            "SLOT_EVENT" => PrinterEventType.SlotEvent,
            "CANCELABLE_CHANGED" => PrinterEventType.CancelableChanged,
            _ => throw new ArgumentOutOfRangeException(nameof(wireValue), wireValue,
                                                       "Not an event type Connect clients send."),
        };
    }

    /// <summary>
    /// The wire word for a domain value — the exact inverse of <see cref="Parse"/>.
    /// <see cref="PrinterEventType.Undefined"/> has no word, deliberately: it is the .NET-only
    /// sentinel and putting it on the wire (or in <c>WireType</c>) would be inventing traffic.
    /// </summary>
    public static string Format(PrinterEventType eventType)
    {
        return eventType switch
        {
            PrinterEventType.Accepted => "ACCEPTED",
            PrinterEventType.Rejected => "REJECTED",
            PrinterEventType.Failed => "FAILED",
            PrinterEventType.Finished => "FINISHED",
            PrinterEventType.Info => "INFO",
            PrinterEventType.StateChanged => "STATE_CHANGED",
            PrinterEventType.StorageEjected => "MEDIUM_EJECTED",
            PrinterEventType.StorageInserted => "MEDIUM_INSERTED",
            PrinterEventType.FileChanged => "FILE_CHANGED",
            PrinterEventType.FileInfo => "FILE_INFO",
            PrinterEventType.JobInfo => "JOB_INFO",
            PrinterEventType.TransferInfo => "TRANSFER_INFO",
            PrinterEventType.MeshBedData => "MESH_BED_DATA",
            PrinterEventType.TransferAborted => "TRANSFER_ABORTED",
            PrinterEventType.TransferStopped => "TRANSFER_STOPPED",
            PrinterEventType.TransferFinished => "TRANSFER_FINISHED",
            PrinterEventType.SlotEvent => "SLOT_EVENT",
            PrinterEventType.CancelableChanged => "CANCELABLE_CHANGED",
            _ => throw new ArgumentOutOfRangeException(nameof(eventType), eventType,
                                                       "No wire word exists for this value."),
        };
    }
}
