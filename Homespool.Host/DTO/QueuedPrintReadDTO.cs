using System;

using Homespool.Model.Entities;

namespace Homespool.Host.DTO;

/// <summary>One entry in a printer's queue, as the API reports it.</summary>
/// <remarks>
/// <para>
/// <b>Deliberately not Connect-shaped.</b> Prusa's spec calls this a <c>PlannedJob</c> and reaches it
/// through <c>get_planned_jobs</c>; <c>/api/v1</c> is ours (Henrik, 2026-07-31 - only <c>/p/*</c> owes
/// Prusa anything), so the vocabulary here is the one the design actually uses.
/// </para>
/// <para>
/// Carries no state field, because a queue entry has no states - it is waiting, or it is gone. What a
/// printer is <i>doing</i> is the printer's, and what a print <i>did</i> will be the job record's.
/// </para>
/// </remarks>
public class QueuedPrintReadDTO
{
    /// <summary>Handle for moving or cancelling this entry.</summary>
    public required long Id { get; set; }

    /// <summary>The file this will print, by the name its owner knows it by.</summary>
    public required string FileName { get; set; }

    /// <summary>Size in bytes, which is roughly how long the transfer will take.</summary>
    public required long Size { get; set; }

    /// <summary>Where in the queue it sits, ascending. Contiguous only until something is cancelled.</summary>
    public required int Position { get; set; }

    public required DateTimeOffset QueuedAt { get; set; }

    /// <summary>
    /// Reads the file's name through the navigation, which the query <c>Include</c>s.
    /// </summary>
    /// <remarks>
    /// The name is a <i>report</i> of what will print, not the reference to it - the entry points at
    /// the file's surrogate id, so a rename between queueing and printing changes what this says and
    /// nothing else.
    /// </remarks>
    public static QueuedPrintReadDTO FromQueuedPrint(QueuedPrint job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new()
        {
            Id = job.Id,
            FileName = job.PrintFile?.Name ?? string.Empty,
            Size = job.PrintFile?.Size ?? 0,
            Position = job.Position,
            QueuedAt = job.QueuedAt,
        };
    }
}
