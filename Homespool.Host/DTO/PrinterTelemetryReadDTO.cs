using System;
using System.Collections.Generic;

namespace Homespool.Host.DTO;

/// <summary>What a printer last said about itself.</summary>
/// <remarks>
/// <para>
/// <b>Read <see cref="Connected"/> and <see cref="LastSeenAt"/> before anything else.</b> The stored
/// state outlives the connection that wrote it, so a printer switched off an hour ago still answers
/// with the temperatures it had then. Nothing here is aged out on the reader's behalf.
/// </para>
/// <para>
/// <b>Numbers are the printer's own, in its own units</b> - °C, percent, seconds, millimetres, RPM -
/// and null means it did not report that one. They are <c>float</c> as they are stored, never widened:
/// a widened float prints as <c>0.40000000596046448</c>.
/// </para>
/// </remarks>
public class PrinterTelemetryReadDTO
{
    /// <summary>Whether the printer has a live connection to this server right now.</summary>
    public required bool Connected { get; set; }

    /// <summary>When the printer last reported. Null when it never has.</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>The printer's state, in the vocabulary <c>GET printers/{uuid}</c> reports it in.</summary>
    public required string State { get; set; }

    /// <summary>What the printer is asking a person to deal with, or null when it is asking nothing.</summary>
    public AttentionReadDTO? Attention { get; set; }

    /// <summary>The running job as the printer counts it, or null when it reports none.</summary>
    /// <remarks>
    /// The printer's view, so it covers a print started at its own panel too - which has no entry in
    /// <c>GET …/jobs</c> until the queue adopts it, and may never have one.
    /// </remarks>
    public JobProgressReadDTO? Job { get; set; }

    public required TemperaturesReadDTO Temperatures { get; set; }

    /// <summary>Print speed, percent.</summary>
    public int? Speed { get; set; }

    /// <summary>Flow, percent.</summary>
    public int? Flow { get; set; }

    public required FansReadDTO Fans { get; set; }

    /// <summary>
    /// Millimetres of filament this printer has ever extruded - a lifetime odometer, not this job's.
    /// </summary>
    /// <remarks>
    /// What one print used is the difference between two readings, and <b>a decrease is the counter
    /// being reset</b> with the printer's EEPROM rather than negative extrusion. It happens once,
    /// unannounced.
    /// </remarks>
    public float? FilamentUsed { get; set; }

    /// <summary>
    /// Every tool the printer has reported, ordered by number. Exactly one on a single-tool printer;
    /// empty on one that has never reported.
    /// </summary>
    public required IReadOnlyList<ToolReadDTO> Tools { get; set; }
}
