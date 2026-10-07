using System;
using System.Collections.Generic;
using System.Threading;

using Homespool.Host.Telemetry;
using Homespool.Model;

namespace Homespool.Host.Notifications;

/// <summary>
/// Notices a printer stopping to wait for a person, from the live-state changes the telemetry writer
/// reports, and hands each one over once it has settled.
/// </summary>
/// <remarks>
/// <para>
/// <b>A change, not a state.</b> A printer that entered Attention an hour ago is not news; one that
/// entered it just now is. Only the writer sees before and after together, which is why this is its
/// observer rather than something that reads the database.
/// </para>
/// <para>
/// <b>Settled before it is announced.</b> The status and the dialog's code arrive separately - the
/// code on a state-change event, the status on the telemetry that follows or precedes it - so a
/// notification sent the instant the status changed would often have no reason to give. Waiting
/// <see cref="Settle"/> lets the code catch up, and lets a dialog that closed again at once cancel
/// itself rather than buzz a phone about nothing.
/// </para>
/// <para>
/// <b>One notification per episode.</b> A filament change shows several dialogs in a row; the code
/// changing while the printer is still waiting is the same episode and says nothing new. Leaving and
/// re-entering does start a new one - except within <see cref="RepeatSuppression"/> for the same
/// dialog on the same job, which is a printer that dropped off and reconnected mid-dialog rather than
/// a second thing to walk over for.
/// </para>
/// <para>
/// <b>A restart announces nothing that was already true.</b> The writer's "before" for a printer's
/// first message after a start is what the database held, so a printer that was waiting when the
/// service went down is still waiting, not newly so.
/// </para>
/// </remarks>
public sealed class AttentionWatch : ILiveStateObserver
{
    /// <summary>How long a printer must have been waiting before it is announced.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    /// <summary>How long the same dialog on the same job is not announced a second time.</summary>
    public static readonly TimeSpan RepeatSuppression = TimeSpan.FromMinutes(30);

    private readonly Lock _lock = new();
    private readonly Dictionary<int, Watched> _printers = [];

    /// <inheritdoc />
    public void Observed(int printerId, LiveStateSnapshot before, LiveStateSnapshot after, DateTimeOffset at)
    {
        lock (_lock)
        {
            if (!_printers.TryGetValue(printerId, out Watched? watched))
            {
                watched = new Watched();
                _printers[printerId] = watched;
            }

            watched.Latest = after;

            if (!NeedsSomeone(after.Status))
            {
                watched.WaitingSince = null;

                return;
            }

            // Entering, or moving between Attention and Error, which are different things to walk over
            // for. Staying put - however the code changes - is the same episode.
            if (!NeedsSomeone(before.Status) || before.Status != after.Status)
            {
                watched.WaitingSince = at;
            }
        }
    }

    /// <summary>
    /// The printers that have been waiting at least <see cref="Settle"/> and have not been announced,
    /// each described as it is now. Each is returned once.
    /// </summary>
    public IReadOnlyList<PrinterNeedsAttention> Due(DateTimeOffset now)
    {
        List<PrinterNeedsAttention> due = [];

        lock (_lock)
        {
            foreach ((int printerId, Watched watched) in _printers)
            {
                if (watched.WaitingSince is not DateTimeOffset since || now - since < Settle)
                {
                    continue;
                }

                watched.WaitingSince = null;

                LiveStateSnapshot state = watched.Latest;
                Episode episode = new(state.Status, state.AttentionCode, state.JobId);

                if (watched.LastAnnounced is Announcement last &&
                    last.Episode == episode &&
                    now - last.At < RepeatSuppression)
                {
                    continue;
                }

                watched.LastAnnounced = new Announcement(episode, now);
                due.Add(new PrinterNeedsAttention(printerId, state.Status, state.AttentionCode, state.AttentionText));
            }
        }

        return due;
    }

    private static bool NeedsSomeone(PrinterStatus status)
    {
        return status is PrinterStatus.Attention or PrinterStatus.Error;
    }

    /// <summary>What makes two waits the same one.</summary>
    private readonly record struct Episode(PrinterStatus Status, int? Code, int? JobId);

    /// <summary>A wait that was announced, and when.</summary>
    private readonly record struct Announcement(Episode Episode, DateTimeOffset At);

    private sealed class Watched
    {
        public LiveStateSnapshot Latest { get; set; }

        public DateTimeOffset? WaitingSince { get; set; }

        public Announcement? LastAnnounced { get; set; }
    }
}
