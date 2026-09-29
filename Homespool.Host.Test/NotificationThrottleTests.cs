using System;

using AwesomeAssertions;

using Homespool.Host.Notifications;
using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// The floor under how often one printer may notify: per printer, per kind, whatever changed in
/// between - and never on a print ending.
/// </summary>
public sealed class NotificationThrottleTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    /// <summary>What the dispatcher does with a happening that reaches somebody: ask, and record.</summary>
    private static bool Admit(NotificationThrottle throttle, PrinterHappening happening, DateTimeOffset now)
    {
        if (!throttle.Allows(happening, now))
        {
            return false;
        }

        throttle.Sent(happening, now);

        return true;
    }

    /// <summary>
    /// Two dialogs taking turns: each is a new wait by the watch's rules, and without the floor each was
    /// a push to every member.
    /// </summary>
    [Fact]
    public void AlternatingDialogsAreSentOncePerGap()
    {
        NotificationThrottle throttle = new();

        Admit(throttle, new PrinterNeedsAttention(1, PrinterStatus.Attention, 23829, null), Start).Should().BeTrue();
        Admit(throttle, new PrinterNeedsAttention(1, PrinterStatus.Attention, 23830, null), Start.AddSeconds(6)).Should().BeFalse();
        Admit(throttle, new PrinterNeedsAttention(1, PrinterStatus.Error, 23201, null), Start.AddSeconds(12)).Should().BeFalse();

        Admit(throttle, new PrinterNeedsAttention(1, PrinterStatus.Attention, 23829, null), Start + NotificationThrottle.AttentionGap)
                .Should().BeTrue("once the gap has passed");
    }

    [Fact]
    public void AFlappingHoldIsSentOncePerGap()
    {
        NotificationThrottle throttle = new();

        Admit(throttle, new QueueHeld(1, PrintHoldReason.InsufficientSpace), Start).Should().BeTrue();
        Admit(throttle, new QueueHeld(1, PrintHoldReason.InsufficientSpace), Start.AddMinutes(1)).Should().BeFalse();
        Admit(throttle, new QueueHeld(1, PrintHoldReason.TransferRefused), Start.AddMinutes(2)).Should().BeFalse();

        Admit(throttle, new QueueHeld(1, PrintHoldReason.InsufficientSpace), Start + NotificationThrottle.QueueHeldGap).Should().BeTrue();
    }

    [Fact]
    public void PrintersAndKindsDoNotShareAFloor()
    {
        NotificationThrottle throttle = new();

        Admit(throttle, new PrinterNeedsAttention(1, PrinterStatus.Attention, 23829, null), Start).Should().BeTrue();

        Admit(throttle, new PrinterNeedsAttention(2, PrinterStatus.Attention, 23829, null), Start).Should().BeTrue("another printer is another floor");
        Admit(throttle, new QueueHeld(1, PrintHoldReason.InsufficientSpace), Start).Should().BeTrue("another kind is another floor");
    }

    /// <summary>
    /// A happening that reached nobody - a hold already gone when it was composed - does not start the
    /// gap, or the next real one would be dropped behind a notification nobody received.
    /// </summary>
    [Fact]
    public void OnlyWhatReachedSomebodyStartsTheGap()
    {
        NotificationThrottle throttle = new();
        QueueHeld held = new(1, PrintHoldReason.InsufficientSpace);

        throttle.Allows(held, Start).Should().BeTrue();

        // Routed, and told nobody: not recorded.
        throttle.Allows(held, Start.AddSeconds(5)).Should().BeTrue();
    }

    [Fact]
    public void PrintEndsAreNeverHeldBack()
    {
        NotificationThrottle throttle = new();

        for (long job = 1; job <= 5; job++)
        {
            Admit(throttle, new PrintEnded(1, job), Start).Should().BeTrue();
        }
    }
}
