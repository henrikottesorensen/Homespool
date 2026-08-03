namespace Homespool.Host.Queue;

/// <summary>What the loop decided to do.</summary>
public sealed record QueueAction
{
    private QueueAction()
    {
    }

    /// <summary>Nothing to do, and nothing being waited for.</summary>
    public static QueueAction Nothing { get; } = new() { Kind = QueueActionKind.Nothing };

    public required QueueActionKind Kind { get; init; }

    /// <summary>The entry acted on, for <see cref="QueueActionKind.Transfer"/> and
    /// <see cref="QueueActionKind.Print"/>.</summary>
    public QueueHead? Head { get; init; }

    /// <summary>Why the loop is waiting, for <see cref="QueueActionKind.Wait"/>.</summary>
    public QueueWaitReason Reason { get; init; }

    public static QueueAction Transfer(QueueHead head)
    {
        return new QueueAction { Kind = QueueActionKind.Transfer, Head = head };
    }

    public static QueueAction Print(QueueHead head)
    {
        return new QueueAction { Kind = QueueActionKind.Print, Head = head };
    }

    public static QueueAction Wait(QueueWaitReason reason)
    {
        return new QueueAction { Kind = QueueActionKind.Wait, Reason = reason };
    }
}
