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

    /// <summary>
    /// Two dialogs taking turns: each is a new wait by the watch's rules, and without the floor each was
    /// a push to every member.
    /// </summary>
    [Fact]
    public void AlternatingDialogsAreSentOncePerGap()
    {
        NotificationThrottle throttle = new();

        throttle.Admit(new PrinterNeedsAttention(1, PrinterStatus.Attention, 23829, null), Start).Should().BeTrue();
        throttle.Admit(new PrinterNeedsAttention(1, PrinterStatus.Attention, 23830, null), Start.AddSeconds(6)).Should().BeFalse();
        throttle.Admit(new PrinterNeedsAttention(1, PrinterStatus.Error, 23201, null), Start.AddSeconds(12)).Should().BeFalse();

        throttle.Admit(new PrinterNeedsAttention(1, PrinterStatus.Attention, 23829, null), Start + NotificationThrottle.AttentionGap)
                .Should().BeTrue("once the gap has passed");
    }

    [Fact]
    public void AFlappingHoldIsSentOncePerGap()
    {
        NotificationThrottle throttle = new();

        throttle.Admit(new QueueHeld(1, PrintHoldReason.InsufficientSpace), Start).Should().BeTrue();
        throttle.Admit(new QueueHeld(1, PrintHoldReason.InsufficientSpace), Start.AddMinutes(1)).Should().BeFalse();
        throttle.Admit(new QueueHeld(1, PrintHoldReason.TransferRefused), Start.AddMinutes(2)).Should().BeFalse();

        throttle.Admit(new QueueHeld(1, PrintHoldReason.InsufficientSpace), Start + NotificationThrottle.QueueHeldGap).Should().BeTrue();
    }

    [Fact]
    public void PrintersAndKindsDoNotShareAFloor()
    {
        NotificationThrottle throttle = new();

        throttle.Admit(new PrinterNeedsAttention(1, PrinterStatus.Attention, 23829, null), Start).Should().BeTrue();

        throttle.Admit(new PrinterNeedsAttention(2, PrinterStatus.Attention, 23829, null), Start).Should().BeTrue("another printer is another floor");
        throttle.Admit(new QueueHeld(1, PrintHoldReason.InsufficientSpace), Start).Should().BeTrue("another kind is another floor");
    }

    [Fact]
    public void PrintEndsAreNeverHeldBack()
    {
        NotificationThrottle throttle = new();

        for (long job = 1; job <= 5; job++)
        {
            throttle.Admit(new PrintEnded(1, job), Start).Should().BeTrue();
        }
    }
}
