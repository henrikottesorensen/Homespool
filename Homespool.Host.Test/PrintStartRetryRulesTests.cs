using System;

using AwesomeAssertions;

using Homespool.Host.Queue;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrintStartRetryRules"/> - how the count of refused starts moves, and when the next
/// attempt may go.
/// </summary>
public class PrintStartRetryRulesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    /// <summary>The same words again add one; anything different starts the count over.</summary>
    [Theory]
    [InlineData("Can't print now", 4)]
    [InlineData("Something firmware has not said before", 1)]
    [InlineData(null, 1)]
    public void AChangingAnswerStartsTheCountOver(string? reason, int expected)
    {
        FileOnPrinter row = Refused(3, "Can't print now");

        PrintStartRetryRules.CountAfter(row, reason).Should().Be(expected);
    }

    /// <summary>
    /// A row with nothing recorded counts its first refusal as one, words or none.
    /// </summary>
    /// <remarks>
    /// The empty row and a refusal with no words both have a null reason, so it is the count, not the
    /// text, that tells them apart.
    /// </remarks>
    [Fact]
    public void TheFirstRefusalCountsAsOne()
    {
        PrintStartRetryRules.CountAfter(new FileOnPrinter(), null).Should().Be(1);
        PrintStartRetryRules.CountAfter(new FileOnPrinter(), "Can't print now").Should().Be(1);
    }

    /// <summary>Text longer than the column still matches its own stored copy.</summary>
    [Fact]
    public void ARefusalLongerThanTheColumnStillCounts()
    {
        string reason = new('x', FileOnPrinter.TransferRefusalReasonMaxLength + 50);
        FileOnPrinter row = Refused(2, RefusalRetries.Bound(reason, FileOnPrinter.TransferRefusalReasonMaxLength));

        PrintStartRetryRules.CountAfter(row, reason).Should().Be(3);
    }

    /// <summary>A refused start waits exactly its delay, and not a tick longer.</summary>
    [Fact]
    public void ARefusedStartWaitsItsDelay()
    {
        FileOnPrinter row = Refused(2, "Can't print now");

        PrintStartRetryRules.IsWaiting(row, Now).Should().BeTrue();
        PrintStartRetryRules.IsWaiting(row, Now + TimeSpan.FromSeconds(11)).Should().BeTrue();
        PrintStartRetryRules.IsWaiting(row, Now + TimeSpan.FromSeconds(12)).Should().BeFalse();
    }

    /// <summary>
    /// A refused transfer is not a refused start: only the start's own columns make the print wait.
    /// </summary>
    [Fact]
    public void ARefusedTransferDoesNotHoldBackAStart()
    {
        FileOnPrinter row = new()
        {
            TransferRefusalCount = 2,
            TransferRefusedAt = Now,
            TransferRefusalReason = "Failed to create directory",
        };

        PrintStartRetryRules.IsWaiting(row, Now).Should().BeFalse();
        PrintStartRetryRules.IsWaiting(null, Now).Should().BeFalse();
    }

    /// <summary>Forgetting clears every refused-start field together, and nothing of the transfer's.</summary>
    [Fact]
    public void ForgettingClearsEveryRefusedStartField()
    {
        FileOnPrinter row = Refused(5, "Can't print now");
        row.TransferRefusalCount = 1;

        PrintStartRetryRules.Forget(row);

        row.StartRefusalCount.Should().BeNull();
        row.StartRefusedAt.Should().BeNull();
        row.StartRefusalReason.Should().BeNull();
        row.TransferRefusalCount.Should().Be(1, "the transfer's count is its own");
    }

    private static FileOnPrinter Refused(int count, string? reason)
    {
        return new FileOnPrinter
        {
            StartRefusalCount = count,
            StartRefusedAt = Now,
            StartRefusalReason = reason,
        };
    }
}
