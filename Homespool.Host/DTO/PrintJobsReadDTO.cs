using System.Collections.Generic;

namespace Homespool.Host.DTO;

/// <summary>What a printer is printing now, and what it has printed.</summary>
/// <remarks>
/// <b>The running print is here as well as the finished ones</b>, because the queue lets go of an
/// entry the moment the printer takes it. Without <see cref="Active"/>, a client following a print by
/// its handle would lose it for exactly as long as it was printing.
/// </remarks>
public class PrintJobsReadDTO
{
    /// <summary>The print running now, or null when there is none.</summary>
    public PrintJobReadDTO? Active { get; set; }

    /// <summary>Finished prints, newest first.</summary>
    public required IReadOnlyList<PrintJobReadDTO> Prints { get; set; }
}
