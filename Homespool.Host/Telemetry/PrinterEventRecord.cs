using Homespool.Model;

namespace Homespool.Host.Telemetry;

/// <summary>
/// One discrete printer event in Homespool's vocabulary - the only event shape
/// <see cref="ITelemetrySink"/> accepts, already mapped and already formatted by the protocol
/// edge that heard it. The writer persists it verbatim as a <c>PrinterEvent</c> row.
/// </summary>
public sealed record PrinterEventRecord
{
    /// <summary>What happened, in the domain vocabulary.</summary>
    public required PrinterEventType EventType { get; init; }

    /// <summary>The wire's own word for it, verbatim - the edge's statement, since only the edge
    /// heard the wire. Null means the event was synthesised rather than heard.</summary>
    public required string? WireType { get; init; }

    /// <summary>Printer state at the time, already domain-mapped.</summary>
    public required PrinterStatus Status { get; init; }

    /// <summary>The job the event belongs to, where job-scoped.</summary>
    public int? JobId { get; init; }

    /// <summary>The command this event answers, where it answers one.</summary>
    public long? CommandId { get; init; }

    /// <summary>The printer's own words, on refusals and failures.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// The event's payload as it should be stored, already serialised - and already reduced or
    /// redacted by the edge, which owns those rules because they are facts about its wire
    /// (a Prusa <c>FILE_INFO</c>'s gcode-header flood, an <c>INFO</c>'s embedded credential).
    /// </summary>
    public string? Payload { get; init; }

    /// <summary>
    /// What this event said about the machine's identity, when it said anything - applied to the
    /// <c>Printer</c> row in the same flush as the event, so the batch semantics stay whole.
    /// </summary>
    public PrinterIdentityUpdate? Identity { get; init; }

    /// <summary>
    /// What this event said about the printer's drive, when it was a directory listing - applied to
    /// the <c>PrinterDriveListing</c> row in the same flush as the event.
    /// </summary>
    /// <remarks>
    /// <b>It travels beside the payload rather than in it, and that is the point.</b> A listing is
    /// superseded by the next one, so it belongs in a row that is replaced rather than in a log that
    /// is appended to - <c>notes/printer-event-bounds.md</c>. The event itself keeps the fact that a
    /// listing arrived, and its <c>file_count</c>.
    /// </remarks>
    public PrinterDriveListingUpdate? DriveListing { get; init; }
}
