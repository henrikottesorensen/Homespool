using System;
using System.Collections.Generic;
using System.Threading;

namespace Homespool.Host.Notifications;

/// <summary>
/// A floor on how often one printer may notify anybody of one kind of thing, whatever noticed it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the sources' own rules cannot stop.</b> <see cref="AttentionWatch"/> suppresses the same
/// wait coming back, and <see cref="NotificationWatcher"/> announces a hold only when it appears - but
/// a printer alternating two dialogs, or a hold flapping with the free space the printer reports, is a
/// new thing every time by those rules, and was a push to every member every few seconds. This counts
/// what was actually sent, per printer and kind, so no pattern of changes can get past it.
/// </para>
/// <para>
/// <b>Dropped, not delayed.</b> A notification held back to be sent later would describe a moment
/// already past; the one that was sent is still on the screen, replaced in place by anything newer
/// with the same printer's tag. A person on their way to the printer loses nothing.
/// </para>
/// <para>
/// <b>Print ends are not throttled</b>: each is one closed print row, so a printer cannot produce them
/// faster than it prints.
/// </para>
/// </remarks>
public sealed class NotificationThrottle
{
    /// <summary>The least time between two notifications that a printer is waiting for somebody.</summary>
    public static readonly TimeSpan AttentionGap = TimeSpan.FromMinutes(5);

    /// <summary>The least time between two notifications that a printer's queue is held.</summary>
    public static readonly TimeSpan QueueHeldGap = TimeSpan.FromMinutes(15);

    private readonly Lock _lock = new();
    private readonly Dictionary<Key, DateTimeOffset> _lastSent = [];

    /// <summary>
    /// Whether <paramref name="happening"/> may be sent now - and if so, counts it as sent.
    /// </summary>
    public bool Admit(PrinterHappening happening, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(happening);

        TimeSpan gap = happening switch
        {
            PrinterNeedsAttention => AttentionGap,
            QueueHeld => QueueHeldGap,
            _ => TimeSpan.Zero,
        };

        if (gap <= TimeSpan.Zero)
        {
            return true;
        }

        Key key = new(happening.PrinterId, happening.GetType());

        lock (_lock)
        {
            if (_lastSent.TryGetValue(key, out DateTimeOffset last) && now - last < gap)
            {
                return false;
            }

            _lastSent[key] = now;

            return true;
        }
    }

    /// <summary>One printer and one kind of happening.</summary>
    private readonly record struct Key(int PrinterId, Type Kind);
}
