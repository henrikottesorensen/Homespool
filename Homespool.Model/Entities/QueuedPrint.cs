using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Homespool.Model.Entities;

/// <summary>
/// One entry in a printer's queue: a file, a printer, a position. Nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Property-less by design</b> (<c>notes/print-queue.md</c>). The first sketch put a state machine
/// here - queued, transferring, ready, printing, done - and Henrik replaced it with the right shape:
/// <b>the printer runs a producer loop and the queue is a list it pulls from</b>. Everything the
/// machine wanted to record belongs somewhere that already owns it. Progress, the firmware job id and
/// the outcome are the <c>Job</c>'s. Paused and Attention are printer states. And whether the bytes
/// have reached the drive belongs to <i>(file, printer)</i>, not to this row - two entries for one
/// file must not transfer it twice.
/// </para>
/// <para>
/// So there is no status column, and cancelling is deleting the row. A row that is gone is a job that
/// will not print; a row that is present is one the loop has not consumed yet.
/// </para>
/// <para>
/// <b>One queue per printer, not per person</b> (Henrik): if you may use the printer you may
/// manipulate its queue - reorder it, cancel from it, anyone's entries. That is how a shared printer
/// is actually used, and it needs no permission this app does not already have.
/// </para>
/// </remarks>
public class QueuedPrint
{
    public long Id { get; set; }

    /// <summary>The printer whose queue this sits in.</summary>
    /// <remarks>
    /// <b>The key alone, with no navigation</b> (2026-08-04), for the reason
    /// <see cref="PrintJob.PrinterId"/> gives: a navigation is a slot EF's relationship fix-up writes
    /// each context's tracked <see cref="Printer"/> into, which poisons any instance outliving the
    /// context that loaded it - and the queue's loop holds these rows across scoped work.
    /// <para>
    /// Nothing lost, because <b>nothing ever asked for it</b>. Unlike <see cref="PrintFile"/>, which is
    /// explicitly <c>Include</c>d where it is read, this slot was only ever populated by fix-up: no
    /// query requested it and no code read it. It was exposure with no corresponding use.
    /// </para>
    /// </remarks>
    public int PrinterId { get; set; }

    /// <summary>
    /// What to print, by surrogate id rather than by name - which is the whole reason
    /// <see cref="PrintFile"/> exists. Renaming a queued file leaves this entry pointing at the same
    /// bytes.
    /// </summary>
    public long PrintFileId { get; set; }

    [ForeignKey(nameof(PrintFileId))]
    public virtual PrintFile? PrintFile { get; set; }

    /// <summary>
    /// Where in the queue it sits, ascending. Ties break by <see cref="Id"/>, so two rows sharing a
    /// position are an ordering nobody chose rather than an ambiguity.
    /// </summary>
    /// <remarks>
    /// A plain integer, rewritten across the affected rows on a reorder. At a queue depth measured in
    /// single digits that is a handful of updates in one <c>SaveChangesAsync</c> - which is one round
    /// trip, so it needs no transaction of its own (<c>notes/transactions.md</c>). A gap-based or
    /// fractional key buys nothing here and costs a rebalancing story.
    /// </remarks>
    public int Position { get; set; }

    /// <summary>
    /// Who put it there, and <b>who the loop acts as when it sends this job's commands</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not state, so it does not break the property-less rule - it is the row's provenance, and it is
    /// load-bearing. The advancer is not a user and must not be a way around
    /// <c>TeamMember.CanUse</c>: it sends through <c>PrinterCommandService</c> as this person, so the
    /// permission is checked again at send time rather than only at enqueue. A member whose access is
    /// revoked between queueing and printing stops advancing, which is the answer that needs no
    /// special case.
    /// </para>
    /// <para>
    /// It is also the only handle on <i>whose</i> file this is: the store is keyed by user id, so
    /// resolving the bytes needs an owner. That is the same person by construction - you can only
    /// queue a file you have.
    /// </para>
    /// </remarks>
    public long QueuedByUserId { get; set; }

    public DateTimeOffset QueuedAt { get; set; }
}
