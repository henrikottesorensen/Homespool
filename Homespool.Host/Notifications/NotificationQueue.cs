using System.Threading.Channels;

using Microsoft.Extensions.Logging;

namespace Homespool.Host.Notifications;

/// <summary>
/// Where happenings wait for <see cref="NotificationDispatcher"/>: bounded, and never a reason for
/// whatever noticed one to wait.
/// </summary>
/// <remarks>
/// <b>Drop-oldest, and that is the right loss.</b> A queue that fills is one whose dispatcher is stuck
/// - a push service not answering - and the happenings nearest the front are the stalest; what a
/// person wants on reconnecting is what is true now. A producer that waited instead would be the
/// telemetry writer or the queue watcher, stalled by a notification.
/// </remarks>
public sealed class NotificationQueue
{
    /// <summary>Far more than a deployment of tens of printers produces while anything is healthy.</summary>
    public const int Capacity = 256;

    private readonly Channel<PrinterHappening> _channel;
    private readonly ILogger<NotificationQueue> _logger;

    public NotificationQueue(ILogger<NotificationQueue> logger)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<PrinterHappening>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            Dropped);
    }

    /// <summary>What the dispatcher reads.</summary>
    public ChannelReader<PrinterHappening> Reader => _channel.Reader;

    /// <summary>Queues a happening. Never waits.</summary>
    public void Publish(PrinterHappening happening)
    {
        _channel.Writer.TryWrite(happening);
    }

    private void Dropped(PrinterHappening happening)
    {
        _logger.LogWarning("[{PrinterId}] a {Happening} notification was dropped: {Capacity} were already waiting to be sent.",
                           happening.PrinterId, happening.GetType().Name, Capacity);
    }
}
