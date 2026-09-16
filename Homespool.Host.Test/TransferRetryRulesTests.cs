using System;

using AwesomeAssertions;

using Homespool.Host.Queue;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="TransferRetryRules"/> - which refusals count towards the hold, and how the attempts
/// between them are spaced.
/// </summary>
public class TransferRetryRulesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    /// <summary>
    /// The busy transfer slot is recognised by its code where there is one, and by its words only
    /// where there is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both halves are measured clients.</b> Buddy sends <c>TRANSFER_IN_PROGRESS</c>; the Python
    /// SDK sends the same words and no code at all. Missing the second would count every busy answer
    /// from an SDK printer and hold its queue behind somebody else's long transfer.
    /// </para>
    /// <para>
    /// <b>A code overrides the words</b>, which is the last row: the prose is free to change between
    /// releases, the code is not, and a printer saying "busy" in a refusal coded as something else is
    /// reporting the something else.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("TRANSFER_IN_PROGRESS", "Another transfer in progress", true)]
    [InlineData("TRANSFER_IN_PROGRESS", null, true)]
    [InlineData(null, "Another transfer in progress", true)]
    [InlineData("", "Another transfer in progress", true)]
    [InlineData(null, "another transfer in progress", false)]
    [InlineData("STORAGE_FAILURE", "Failed to create directory", false)]
    [InlineData(null, null, false)]
    [InlineData("STORAGE_FAILURE", "Another transfer in progress", false)]
    public void OnlyTheBusySlotIsExcluded(string? code, string? reason, bool busy)
    {
        TransferRetryRules.IsBusySlot(code, reason).Should().Be(busy);
    }

    /// <summary>The same answer again adds one; anything different starts the count over.</summary>
    [Theory]
    [InlineData("STORAGE_FAILURE", "Failed to create directory", 4)]
    [InlineData("STORAGE_FAILURE", "Failed to open file", 1)]
    [InlineData("NOT_READY", "Failed to create directory", 1)]
    [InlineData(null, "Failed to create directory", 1)]
    public void AChangingAnswerStartsTheCountOver(string? code, string? reason, int expected)
    {
        PrintFileOnPrinter row = Refused(3, "STORAGE_FAILURE", "Failed to create directory");

        TransferRetryRules.CountAfter(row, code, reason).Should().Be(expected);
    }

    /// <summary>A row with nothing recorded counts its first refusal as one.</summary>
    [Fact]
    public void TheFirstRefusalCountsAsOne()
    {
        TransferRetryRules.CountAfter(new PrintFileOnPrinter(), null, null)
                          .Should().Be(1, "an empty row and a refusal with no words must not look alike");
    }

    /// <summary>
    /// Text longer than the column still matches its own stored copy.
    /// </summary>
    /// <remarks>
    /// The stored value has been cut to the bound, so comparing the incoming text uncut would never
    /// match, and a printer with a long refusal would retry for ever - the defect this whole class
    /// exists to end.
    /// </remarks>
    [Fact]
    public void ARefusalLongerThanTheColumnStillCounts()
    {
        string reason = new('x', PrintFileOnPrinter.TransferRefusalReasonMaxLength + 50);
        PrintFileOnPrinter row = Refused(2, "STORAGE_FAILURE",
                                         TransferRetryRules.Bound(reason, PrintFileOnPrinter.TransferRefusalReasonMaxLength));

        TransferRetryRules.CountAfter(row, "STORAGE_FAILURE", reason).Should().Be(3);
    }

    /// <summary>
    /// The waits are the schedule the bound was sized against, and the bound falls after the last.
    /// </summary>
    /// <remarks>
    /// Six refusals with five waits between them: 6 + 12 + 30 + 60 + 120 seconds, a little under four
    /// minutes from the first refusal to the hold.
    /// </remarks>
    [Fact]
    public void TheScheduleIsFiveRetriesAcrossUnderFourMinutes()
    {
        TimeSpan total = TimeSpan.Zero;

        for (int count = 1; count < TransferRetryRules.HoldAfter; count++)
        {
            total += TransferRetryRules.WaitAfter(count);
        }

        TransferRetryRules.HoldAfter.Should().Be(6);
        TransferRetryRules.WaitAfter(1).Should().Be(TimeSpan.FromSeconds(6));
        TransferRetryRules.WaitAfter(5).Should().Be(TimeSpan.FromSeconds(120));
        total.Should().Be(TimeSpan.FromSeconds(228));
    }

    /// <summary>A count outside the schedule is clamped rather than thrown on.</summary>
    [Theory]
    [InlineData(0, 6)]
    [InlineData(-1, 6)]
    [InlineData(99, 120)]
    public void ACountOutsideTheScheduleIsClamped(int count, int seconds)
    {
        TransferRetryRules.WaitAfter(count).Should().Be(TimeSpan.FromSeconds(seconds));
    }

    /// <summary>A refused row waits exactly its delay, and not a tick longer.</summary>
    [Fact]
    public void ARefusedTransferWaitsItsDelay()
    {
        PrintFileOnPrinter row = Refused(2, "STORAGE_FAILURE", "Failed to create directory");

        TransferRetryRules.IsWaiting(row, Now).Should().BeTrue();
        TransferRetryRules.IsWaiting(row, Now + TimeSpan.FromSeconds(11)).Should().BeTrue();
        TransferRetryRules.IsWaiting(row, Now + TimeSpan.FromSeconds(12)).Should().BeFalse();
    }

    /// <summary>Nothing recorded, nothing to wait for.</summary>
    [Fact]
    public void ARowWithNoRefusalIsNotWaiting()
    {
        TransferRetryRules.IsWaiting(null, Now).Should().BeFalse();
        TransferRetryRules.IsWaiting(new PrintFileOnPrinter(), Now).Should().BeFalse();
    }

    /// <summary>
    /// Cutting printer text never leaves half a surrogate pair behind.
    /// </summary>
    /// <remarks>
    /// A lone high surrogate is not valid UTF-16, so the column would hold a string no encoder can
    /// write out cleanly - on the page or in a log line.
    /// </remarks>
    [Fact]
    public void BoundingDoesNotSplitASurrogatePair()
    {
        string value = new string('a', 9) + "😀";

        string? bounded = TransferRetryRules.Bound(value, 10);

        bounded.Should().Be(new string('a', 9));
        TransferRetryRules.Bound("short", 10).Should().Be("short");
        TransferRetryRules.Bound(null, 10).Should().BeNull();
    }

    /// <summary>Forgetting clears every refusal field together.</summary>
    [Fact]
    public void ForgettingClearsEveryRefusalField()
    {
        PrintFileOnPrinter row = Refused(5, "STORAGE_FAILURE", "Failed to create directory");

        TransferRetryRules.Forget(row);

        row.TransferRefusalCount.Should().BeNull();
        row.TransferRefusedAt.Should().BeNull();
        row.TransferRefusalCode.Should().BeNull();
        row.TransferRefusalReason.Should().BeNull();
    }

    private static PrintFileOnPrinter Refused(int count, string? code, string? reason)
    {
        return new PrintFileOnPrinter
        {
            TransferRefusalCount = count,
            TransferRefusedAt = Now,
            TransferRefusalCode = code,
            TransferRefusalReason = reason,
        };
    }
}
