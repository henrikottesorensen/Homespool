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
}
