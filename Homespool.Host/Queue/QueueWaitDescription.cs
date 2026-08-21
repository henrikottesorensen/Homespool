using Homespool.Host.Localisation;

namespace Homespool.Host.Queue;

/// <summary>
/// Names the sentence for a <see cref="QueueWaitReason"/>, or null when something else on the page
/// already says it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Null is a real answer here.</b> Saying the same thing twice is worse than saying it once: the
/// active print already announces itself as <i>starting</i>, a disconnected printer already has a
/// badge, and an empty queue already says so. Repeating those under the queue would make a page that
/// nags rather than informs.
/// </para>
/// <para>
/// <b>No percentages.</b> Nothing persists transfer progress yet, so "sending" is the whole of what
/// can honestly be said - see <c>file-storage.md</c>, "Still not done".
/// </para>
/// <para>
/// The one worth reading twice is <see cref="QueueWaitReason.PrinterNotAvailable"/>. It carries a rule
/// nothing else on the page states - that only a person offers a printer up for work - and that rule
/// is exactly what makes a correctly-working queue look broken to somebody who does not know it.
/// </para>
/// </remarks>
public static class QueueWaitDescription
{
    /// <summary>Which sentence a decision wants, or null when the page should stay quiet.</summary>
    /// <remarks>
    /// A key rather than the words, so the page decides the language - the same trade the hold
    /// reason makes one layer down. See <see cref="MessageKey"/>.
    /// </remarks>
    public static MessageKey? For(QueueAction action, string? fileName)
    {
        System.ArgumentNullException.ThrowIfNull(action);

        if (action.Kind != QueueActionKind.Wait)
        {
            return null;
        }

        return action.Reason switch
        {
            // The file name is only known some of the time, so the nameless form is a separate key
            // rather than a placeholder filled with "the next file" - a translator needs to see the
            // whole sentence to word either of them.
            QueueWaitReason.Transferring => fileName is null ?
                MessageKey.For("Queue_WaitTransferringUnnamed") :
                MessageKey.For("Queue_WaitTransferring", fileName),
            QueueWaitReason.AwaitingPrinterPath => MessageKey.For("Queue_WaitAwaitingPath"),
            QueueWaitReason.PrinterNotAvailable => MessageKey.For("Queue_WaitPrinterNotReady"),

            // InsufficientSpace has its own banner, carrying the two numbers; PrintStarting is already
            // on the page as the active print. Both would be a second voice saying the same thing.
            _ => null,
        };
    }

    /// <summary>
    /// Whether a wait is the queue stopped on a <em>person</em> rather than on itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Of the reasons this class gives a sentence to</b>, and that is the whole scope: transferring
    /// and awaiting-a-path are the loop working and clear themselves, so they want a footnote.
    /// <see cref="QueueWaitReason.PrinterNotAvailable"/> clears when somebody says the printer may
    /// take work, and nothing else on the page states that rule.
    /// </para>
    /// <para>
    /// <b>InsufficientSpace also needs a person and is deliberately false</b> - it is not a case this
    /// predicate covers, because <see cref="For"/> gives it no sentence at all. Its own banner carries
    /// the two numbers, and answering true here would put a second voice beside it.
    /// </para>
    /// <para>
    /// <b>Written after somebody sat wondering why a queued print had not started</b> (Henrik,
    /// 2026-08-21). The printer was <c>Idle</c>, the sentence was on the page, and it was grey
    /// footnote text under the temperature tiles. The words were right and the weight was wrong.
    /// </para>
    /// </remarks>
    public static bool NeedsAPerson(QueueWaitReason? reason)
    {
        return reason == QueueWaitReason.PrinterNotAvailable;
    }
}
