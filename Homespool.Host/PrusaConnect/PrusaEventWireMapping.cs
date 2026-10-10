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
/// because each value maps to exactly one word. That holds only because an unknown word never
/// reaches persistence at all: it has no value to format back.
/// </para>
/// <para>
/// <b>An unknown word costs its one message, never the connection.</b> The vocabulary does grow —
/// <c>CANCELABLE_CHANGED</c> above is firmware's own addition — so a release can start sending a word
/// this table lacks, and keep sending it. <c>MessageDispatcher</c> asks <see cref="TryParse"/>
/// before deserialising and drops such an event with a throttled warning naming the word, the same
/// posture as an unknown state in <c>ParseWireState</c>. Guessing a value instead would be worse
/// than losing the message: the event log would carry a fact the printer never reported.
/// </para>
/// </remarks>
public static class PrusaEventWireMapping
{
    /// <summary>
    /// Parses a wire event word. Throws on anything outside the known vocabulary rather than
    /// guessing — see the class remarks for why a guess is worse than a refusal.
    /// </summary>
    public static PrinterEventType Parse(string wireValue)
    {
        if (!TryParse(wireValue, out PrinterEventType eventType))
        {
            throw new ArgumentOutOfRangeException(nameof(wireValue), wireValue,
                                                  "Not an event type Connect clients send.");
        }

        return eventType;
    }

    /// <summary>
    /// Parses a wire event word, or says it is outside the known vocabulary — for the caller that
    /// drops such a message rather than failing on it.
    /// </summary>
    /// <param name="wireValue">The word as it arrived.</param>
    /// <param name="eventType">The domain value, or <see cref="PrinterEventType.Undefined"/> when
    /// the word is unknown.</param>
    /// <returns>Whether the word is one Connect clients send.</returns>
    public static bool TryParse(string wireValue, out PrinterEventType eventType)
    {
        eventType = wireValue switch
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
            _ => PrinterEventType.Undefined,
        };

        return eventType != PrinterEventType.Undefined;
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
