using System.Collections.Generic;

namespace Homespool.Host.DTO;

/// <summary>A printer's queue, and what it is waiting on.</summary>
/// <remarks>
/// <para>
/// <b>An object rather than a bare array</b>, which is what this endpoint used to return. A client
/// watching a queue that is not moving has the same problem a person does - it can see the entries and
/// not the reason - and a list has nowhere to put the answer.
/// </para>
/// <para>
/// <see cref="Waiting"/> is computed from the same snapshot the loop reads, through the same rules, so
/// it cannot disagree with what the loop is doing. Null means nothing needs saying: the queue is
/// moving, or the reason is one a caller can already see elsewhere - a print that is starting, or a
/// printer that is not connected.
/// </para>
/// </remarks>
public class PrintQueueReadDTO
{
    /// <summary>A sentence for a person, or null when the queue needs no explanation.</summary>
    public string? Waiting { get; set; }

    /// <summary>What this printer will print, in order.</summary>
    public required IReadOnlyList<QueuedPrintReadDTO> Prints { get; set; }
}
