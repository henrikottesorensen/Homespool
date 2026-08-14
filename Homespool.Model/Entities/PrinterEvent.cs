using System;
using System.ComponentModel.DataAnnotations;

namespace Homespool.Model.Entities;

/// <summary>
/// A discrete event reported by a printer — <c>JOB_INFO</c>, <c>FILE_INFO</c>, <c>INFO</c>,
/// transfer notifications, command acknowledgements, and so on.
/// </summary>
/// <remarks>
/// <para>
/// Events are not time-series and do not belong in <see cref="TelemetrySample"/>. Their
/// payloads vary wildly by type — <c>FILE_INFO</c> alone carries slicer settings, filament
/// costs and a preview thumbnail — so the payload is kept as raw JSON rather than exploded
/// into a table per event type. Query it with SQLite's JSON functions when needed.
/// </para>
/// <para>
/// Events are low-volume (5 in the entire ~2100-message <c>websucket</c> capture) and are
/// retained indefinitely, unlike samples.
/// </para>
/// </remarks>
public class PrinterEvent
{
    [Key]
    public long Id { get; set; }

    // Deliberately no Printer navigation, only the FK - same reasoning as TelemetrySample: these
    // rows survive failed flushes in TelemetryWriter's retry buffer, and a navigation is where
    // fix-up parks a dead context's Printer instance.
    public int PrinterId { get; set; }

    /// <summary>When the event was received by the server.</summary>
    public DateTimeOffset Timestamp { get; set; }

    public PrinterEventType EventType { get; set; }

    /// <summary>
    /// The wire's own word for this event, verbatim — <c>"TRANSFER_INFO"</c>, not
    /// <see cref="PrinterEventType.TransferInfo"/>'s name. Null means the event was synthesised by
    /// Homespool rather than heard from a printer.
    /// </summary>
    /// <remarks>
    /// <see cref="EventType"/> is Homespool's vocabulary and the mapping into it is lossy by
    /// design; this column is what keeps history honest about what the printer actually said,
    /// whichever protocol said it.
    /// </remarks>
    public string? WireType { get; set; }

    /// <summary>Printer state at the time of the event; every observed event carries one.</summary>
    public PrinterStatus Status { get; set; }

    /// <summary>Set on job-scoped events.</summary>
    public int? JobId { get; set; }

    /// <summary>
    /// The command this event responds to, where applicable. Every event in the capture carried
    /// one, including <c>ACCEPTED</c>/<c>REJECTED</c>/<c>FINISHED</c> acknowledgements.
    /// </summary>
    public long? CommandId { get; set; }

    /// <summary>Populated on <c>REJECTED</c> and <c>FAILED</c>.</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// The event's <c>data</c> object, verbatim from the wire. Null for events that carry none.
    /// </summary>
    /// <remarks>
    /// One exception to "verbatim": a <c>FILE_INFO</c>'s <c>preview</c> - a base64 PNG thumbnail
    /// dominating the message at 47-89 KB - is stored as <c>null</c> rather than kept, because these
    /// rows are never pruned and the printer sends them unasked. The key is nulled rather than
    /// removed so that "we dropped a thumbnail" stays distinguishable from "this file had none",
    /// which is what firmware itself expresses by omitting the key. See
    /// <c>TelemetryWriter.FormatPayload</c>.
    /// </remarks>
    public string? Payload { get; set; }
}
