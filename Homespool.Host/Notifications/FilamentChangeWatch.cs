using System;
using System.Collections.Generic;
using System.Threading;

using Homespool.Host.Telemetry;

namespace Homespool.Host.Notifications;

/// <summary>
/// Notices a print's countdown to its next filament change crossing <see cref="Threshold"/>, and
/// says so at once.
/// </summary>
/// <remarks>
/// <para>
/// <b>The only predictive signal the printer sends.</b> Everything else describes the present; this
/// says when the printer will stop and wait for a person - which is why Prusa's own app warns at five
/// minutes, and why this does too. When the stop comes, <see cref="AttentionWatch"/> says that.
/// </para>
/// <para>
/// <b>A crossing, not a value.</b> A countdown already under the threshold before the message is not
/// news, which is what keeps a restart mid-countdown quiet: the writer's "before" for the first message
/// is what the database held. A countdown that <i>appears</i> under it is news - a print whose first
/// change is minutes after it starts - but one appearing far out is not, which is the reason the
/// notification design gave for not alerting at the start of a print with a swap at hour six.
/// </para>
/// <para>
/// <b>Re-armed with a margin.</b> The printer's estimate wobbles, and a countdown hovering at the
/// threshold would otherwise cross it again and again. It re-arms only once the countdown is gone - the
/// change happened - or has risen <see cref="Rearm"/> clear of the threshold, which is the next change
/// being counted down.
/// </para>
/// <para>
/// Published straight to the queue rather than held for the watcher, unlike an attention: there is no
/// second fact to wait for, and publishing never waits.
/// </para>
/// </remarks>
public sealed class FilamentChangeWatch : ILiveStateObserver
{
    /// <summary>How long before a change the notification goes: long enough to walk over.</summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromMinutes(5);

    /// <summary>How far above the threshold the countdown must rise before it can cross again.</summary>
    public static readonly TimeSpan Rearm = TimeSpan.FromMinutes(1);

    private readonly NotificationQueue _queue;
    private readonly Lock _lock = new();
    private readonly HashSet<int> _announced = [];

    public FilamentChangeWatch(NotificationQueue queue)
    {
        _queue = queue;
    }

    /// <inheritdoc />
    public void Observed(int printerId, LiveStateSnapshot before, LiveStateSnapshot after, DateTimeOffset at)
    {
        int threshold = (int)Threshold.TotalSeconds;

        lock (_lock)
        {
            if (after.TimeToFilamentChange is not { } left || left > threshold + (int)Rearm.TotalSeconds)
            {
                _announced.Remove(printerId);
            }

            bool crossed = after.TimeToFilamentChange is { } now &&
                           now <= threshold &&
                           (before.TimeToFilamentChange is not { } was || was > threshold);

            if (!crossed || !_announced.Add(printerId))
            {
                return;
            }
        }

        _queue.Publish(new FilamentChangeSoon(printerId, after.TimeToFilamentChange!.Value));
    }
}
