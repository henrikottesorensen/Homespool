using System;
using System.Collections.Generic;

using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.DTO;

/// <summary>One print: what ran, whose it was, and how it went.</summary>
/// <remarks>
/// <para>
/// <b>A record of what happened, not a pointer at anything.</b> <see cref="FileName"/> is the name the
/// file had when the print started; the file may since have been renamed, replaced or deleted.
/// </para>
/// <para>
/// <b>Deliberately without</b> the row's own id (printers, files and people are reached by handle
/// everywhere in this API), the printer's own job id and path (the printer's id space, not ours), and
/// the authority the work was accepted under, which is the server's business.
/// </para>
/// </remarks>
public class PrintJobReadDTO
{
    /// <summary>
    /// The handle the print was queued under - the same one the queue listed it by.
    /// </summary>
    /// <remarks>
    /// <b>Not unique in a listing.</b> One enqueue can leave several rows: a full drive or a refused
    /// transfer records a <c>Failed</c> attempt while the entry stays queued, and the print that
    /// follows records another. Rows sharing a handle are everything that became of one enqueue.
    /// </remarks>
    public required Guid PrintUuid { get; set; }

    public required string FileName { get; set; }

    /// <summary>The file's SHA-384 at the time, when known - so two prints can be told apart as bytes.</summary>
    public string? Digest { get; set; }

    /// <summary>
    /// <c>Starting</c> or <c>Printing</c> while it runs; <c>Finished</c>, <c>Stopped</c>, <c>Failed</c>,
    /// <c>Unconfirmed</c> or <c>Unknown</c> once it has ended.
    /// </summary>
    public required string State { get; set; }

    /// <summary>Who queued it. Null only when the account can no longer be named.</summary>
    public UserReferenceReadDTO? QueuedBy { get; set; }

    /// <summary>
    /// Who stopped it from here. Null when nobody here did - it was stopped at the printer, or ended
    /// some other way.
    /// </summary>
    public UserReferenceReadDTO? StoppedBy { get; set; }

    /// <summary>When the print began, or - for a print begun at the printer - when it was noticed.</summary>
    public required DateTimeOffset StartedAt { get; set; }

    /// <summary>When Homespool sent the start. Null means the print was started at the printer.</summary>
    public DateTimeOffset? CommandedAt { get; set; }

    /// <summary>When it stopped printing. Null means it is running now.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>
    /// Why it ended badly or never began, in the printer's own words where it gave any. Not translated.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Whether the caller may print this again - it has to be theirs, and they have to be allowed to
    /// queue on this printer.
    /// </summary>
    public required bool CanReprint { get; set; }

    public static PrintJobReadDTO FromPrintJob(PrintJob job,
                                               IReadOnlyDictionary<long, UserReference> people,
                                               bool canReprint)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(people);

        return new()
        {
            PrintUuid = job.PrintUuid,
            FileName = job.FileName,
            Digest = job.Digest,
            State = job.State.ToString(),
            QueuedBy = Person(people, job.QueuedByUserId),
            StoppedBy = job.StoppedByUserId is { } stopper ? Person(people, stopper) : null,
            StartedAt = job.StartedAt,
            CommandedAt = job.CommandedAt,
            EndedAt = job.EndedAt,
            Reason = job.Reason,
            CanReprint = canReprint,
        };
    }

    private static UserReferenceReadDTO? Person(IReadOnlyDictionary<long, UserReference> people, long userId)
    {
        return people.TryGetValue(userId, out UserReference? reference) ?
            UserReferenceReadDTO.FromReference(reference) :
            null;
    }
}
