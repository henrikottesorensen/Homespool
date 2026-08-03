namespace Homespool.Host.Queue;

/// <summary>
/// Turns a <see cref="QueueWaitReason"/> into a sentence for a person, or null when something else on
/// the page already says it.
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
    /// <summary>The sentence for a decision, or null when the page should stay quiet.</summary>
    public static string? For(QueueAction action, string? fileName)
    {
        System.ArgumentNullException.ThrowIfNull(action);

        if (action.Kind != QueueActionKind.Wait)
        {
            return null;
        }

        string file = fileName ?? "the next file";

        return action.Reason switch
        {
            QueueWaitReason.Transferring => $"Sending {file} to the printer.",
            QueueWaitReason.AwaitingPrinterPath => "Waiting for the printer to confirm the file.",
            QueueWaitReason.PrinterNotAvailable => "Waiting for the printer to be made ready.",

            // InsufficientSpace has its own banner, carrying the two numbers; PrintStarting is already
            // on the page as the active print. Both would be a second voice saying the same thing.
            _ => null,
        };
    }
}
