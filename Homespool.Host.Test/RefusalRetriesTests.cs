using System;

using AwesomeAssertions;

using Homespool.Host.Queue;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="RefusalRetries"/> - the schedule a refused transfer and a refused print start share,
/// and the bound on the printer's words kept beside either.
/// </summary>
public class RefusalRetriesTests
{
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

        for (int count = 1; count < RefusalRetries.HoldAfter; count++)
        {
            total += RefusalRetries.WaitAfter(count);
        }

        RefusalRetries.HoldAfter.Should().Be(6);
        RefusalRetries.WaitAfter(1).Should().Be(TimeSpan.FromSeconds(6));
        RefusalRetries.WaitAfter(5).Should().Be(TimeSpan.FromSeconds(120));
        total.Should().Be(TimeSpan.FromSeconds(228));
    }

    /// <summary>A count outside the schedule is clamped rather than thrown on.</summary>
    [Theory]
    [InlineData(0, 6)]
    [InlineData(-1, 6)]
    [InlineData(99, 120)]
    public void ACountOutsideTheScheduleIsClamped(int count, int seconds)
    {
        RefusalRetries.WaitAfter(count).Should().Be(TimeSpan.FromSeconds(seconds));
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

        string? bounded = RefusalRetries.Bound(value, 10);

        bounded.Should().Be(new string('a', 9));
        RefusalRetries.Bound("short", 10).Should().Be("short");
        RefusalRetries.Bound(null, 10).Should().BeNull();
    }
}
