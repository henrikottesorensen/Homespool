using System;
using System.Collections.Generic;

using AwesomeAssertions;

using Homespool.Host.Notifications;
using Homespool.Host.Telemetry;
using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// When a printer waiting for somebody is news: entering the wait, settled, once per episode - and
/// never a state that was already true before the service started.
/// </summary>
public sealed class AttentionWatchTests
{
    private const int Printer = 7;
    private const int RunoutCode = 23829;

    private static readonly DateTimeOffset Start = new(2026, 9, 28, 20, 0, 0, TimeSpan.Zero);

    private static LiveStateSnapshot State(PrinterStatus status, int? code = null, int? job = 42)
    {
        return new LiveStateSnapshot(status, code, null, job);
    }

    private static List<PrinterNeedsAttention> DueAt(AttentionWatch watch, TimeSpan after)
    {
        return [.. watch.Due(Start + after)];
    }

    [Fact]
    public void EnteringTheWaitIsAnnouncedOnceItHasSettled()
    {
        AttentionWatch watch = new();

        watch.Observed(Printer, State(PrinterStatus.Printing), State(PrinterStatus.Attention, RunoutCode), Start);

        DueAt(watch, AttentionWatch.Settle - TimeSpan.FromSeconds(1)).Should().BeEmpty("it has not settled yet");

        PrinterNeedsAttention due = DueAt(watch, AttentionWatch.Settle).Should().ContainSingle().Subject;
        due.Should().Be(new PrinterNeedsAttention(Printer, PrinterStatus.Attention, RunoutCode, null));

        DueAt(watch, AttentionWatch.Settle * 2).Should().BeEmpty("each wait is handed over once");
    }

    /// <summary>
    /// The status and the code arrive separately; the one announced is the state once both are in.
    /// </summary>
    [Fact]
    public void ACodeArrivingAfterTheStatusIsTheOneAnnounced()
    {
        AttentionWatch watch = new();

        watch.Observed(Printer, State(PrinterStatus.Printing), State(PrinterStatus.Attention), Start);
        watch.Observed(Printer, State(PrinterStatus.Attention), State(PrinterStatus.Attention, RunoutCode), Start.AddSeconds(1));

        DueAt(watch, AttentionWatch.Settle).Should().ContainSingle().Which.Code.Should().Be(RunoutCode);
    }

    [Fact]
    public void AWaitThatEndsBeforeItSettlesIsNeverAnnounced()
    {
        AttentionWatch watch = new();

        watch.Observed(Printer, State(PrinterStatus.Printing), State(PrinterStatus.Attention, RunoutCode), Start);
        watch.Observed(Printer, State(PrinterStatus.Attention, RunoutCode), State(PrinterStatus.Printing), Start.AddSeconds(2));

        DueAt(watch, AttentionWatch.Settle * 3).Should().BeEmpty();
    }

    /// <summary>
    /// A filament change is several dialogs in a row; the printer is still waiting, and a second
    /// notification would say nothing the first did not.
    /// </summary>
    [Fact]
    public void ANewDialogInTheSameWaitIsNotASecondNotification()
    {
        AttentionWatch watch = new();

        watch.Observed(Printer, State(PrinterStatus.Printing), State(PrinterStatus.Attention, RunoutCode), Start);
        DueAt(watch, AttentionWatch.Settle).Should().ContainSingle();

        watch.Observed(Printer, State(PrinterStatus.Attention, RunoutCode), State(PrinterStatus.Attention, 23830), Start.AddMinutes(1));

        DueAt(watch, TimeSpan.FromMinutes(2)).Should().BeEmpty();
    }

    /// <summary>
    /// A printer that dropped off and came back mid-dialog is the same wait, not a second thing to
    /// walk over for - but the same dialog on another job is.
    /// </summary>
    [Fact]
    public void TheSameDialogOnTheSameJobIsNotAnnouncedTwiceInAShortWhile()
    {
        AttentionWatch watch = new();

        watch.Observed(Printer, State(PrinterStatus.Printing), State(PrinterStatus.Attention, RunoutCode), Start);
        DueAt(watch, AttentionWatch.Settle).Should().ContainSingle();

        watch.Observed(Printer, State(PrinterStatus.Attention, RunoutCode), State(PrinterStatus.Offline), Start.AddMinutes(1));
        watch.Observed(Printer, State(PrinterStatus.Offline), State(PrinterStatus.Attention, RunoutCode), Start.AddMinutes(2));
        DueAt(watch, TimeSpan.FromMinutes(3)).Should().BeEmpty("a reconnect mid-dialog is not news");

        watch.Observed(Printer, State(PrinterStatus.Attention, RunoutCode), State(PrinterStatus.Printing, job: 43), Start.AddMinutes(4));
        watch.Observed(Printer, State(PrinterStatus.Printing, job: 43), State(PrinterStatus.Attention, RunoutCode, job: 43), Start.AddMinutes(5));
        DueAt(watch, TimeSpan.FromMinutes(6)).Should().ContainSingle("the next print running out is");
    }

    [Fact]
    public void TheSameDialogIsAnnouncedAgainOnceTheSuppressionHasPassed()
    {
        AttentionWatch watch = new();

        watch.Observed(Printer, State(PrinterStatus.Printing), State(PrinterStatus.Attention, RunoutCode), Start);
        DueAt(watch, AttentionWatch.Settle).Should().ContainSingle();

        TimeSpan later = AttentionWatch.RepeatSuppression + TimeSpan.FromMinutes(1);
        watch.Observed(Printer, State(PrinterStatus.Attention, RunoutCode), State(PrinterStatus.Printing), Start + later);
        watch.Observed(Printer, State(PrinterStatus.Printing), State(PrinterStatus.Attention, RunoutCode), Start + later);

        DueAt(watch, later + AttentionWatch.Settle).Should().ContainSingle();
    }

    [Fact]
    public void AnErrorAfterAnAttentionIsItsOwnNotification()
    {
        AttentionWatch watch = new();

        watch.Observed(Printer, State(PrinterStatus.Printing), State(PrinterStatus.Attention, RunoutCode), Start);
        DueAt(watch, AttentionWatch.Settle).Should().ContainSingle();

        watch.Observed(Printer, State(PrinterStatus.Attention, RunoutCode), State(PrinterStatus.Error, 23201), Start.AddMinutes(1));

        DueAt(watch, TimeSpan.FromMinutes(2)).Should().ContainSingle().Which.Status.Should().Be(PrinterStatus.Error);
    }

    /// <summary>
    /// After a restart the writer's "before" is what the database held, so a printer that was already
    /// waiting is still waiting, not newly so.
    /// </summary>
    [Fact]
    public void AWaitThatWasAlreadyTrueIsNotNews()
    {
        AttentionWatch watch = new();

        watch.Observed(Printer, State(PrinterStatus.Attention, RunoutCode), State(PrinterStatus.Attention, RunoutCode), Start);

        DueAt(watch, AttentionWatch.Settle * 2).Should().BeEmpty();
    }

    [Theory]
    [InlineData(PrinterStatus.Paused)]
    [InlineData(PrinterStatus.Finished)]
    [InlineData(PrinterStatus.Stopped)]
    [InlineData(PrinterStatus.Idle)]
    [InlineData(PrinterStatus.Offline)]
    public void OtherStopsAreNotAWaitForSomebody(PrinterStatus status)
    {
        AttentionWatch watch = new();

        watch.Observed(Printer, State(PrinterStatus.Printing), State(status), Start);

        DueAt(watch, AttentionWatch.Settle * 2).Should().BeEmpty();
    }

    [Fact]
    public void MutedKindsRoundTripAndUnknownNamesAreIgnored()
    {
        string? stored = NotificationMutes.Format([NotificationKind.QueueHeld, NotificationKind.PrintFinished, NotificationKind.QueueHeld]);

        stored.Should().Be("PrintFinished QueueHeld");
        NotificationMutes.Parse(stored).Should().BeEquivalentTo([NotificationKind.PrintFinished, NotificationKind.QueueHeld]);

        NotificationMutes.Format([]).Should().BeNull("nothing turned off is stored as nothing");
        NotificationMutes.Parse("QueueHeld SomethingRemoved Undefined").Should().BeEquivalentTo([NotificationKind.QueueHeld]);
        NotificationMutes.Parse(null).Should().BeEmpty();
    }
}
