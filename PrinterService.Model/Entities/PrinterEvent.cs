using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrinterService.Model.Entities;

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

    public int PrinterId { get; set; }

    [ForeignKey(nameof(PrinterId))]
    public virtual Printer? Printer { get; set; }

    /// <summary>When the event was received by the server.</summary>
    public DateTimeOffset Timestamp { get; set; }

    public Events EventType { get; set; }

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
    public string? Payload { get; set; }
}
