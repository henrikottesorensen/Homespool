using System;

using AwesomeAssertions;

using Homespool.Host.Localisation;

namespace Homespool.Host.Test;

/// <summary>
/// "3 minutes ago" - the line that stops a self-refreshing card passing for a live one.
/// </summary>
public sealed class RelativeTimeTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 18, 0, 0, TimeSpan.Zero);

    private static readonly RelativeTimeText Ages = new(TestLocaliser.Shared());

    [Fact]
    public void AMomentAgoIsJustNow()
    {
        Ages.Since(Now.AddSeconds(-2), Now).Should().Be("just now");
    }

    /// <summary>
    /// A batch can be written a moment ahead of the clock the page reads. "in -2 seconds" is a worse
    /// answer than a harmless rounding.
    /// </summary>
    [Fact]
    public void TheFutureReadsAsJustNowRatherThanNegative()
    {
        Ages.Since(Now.AddSeconds(3), Now).Should().Be("just now");
    }

    [Fact]
    public void SecondsAreCounted()
    {
        Ages.Since(Now.AddSeconds(-42), Now).Should().Be("42 seconds ago");
    }

    /// <summary>The singular form exists and is used, which is the whole reason Plural does.</summary>
    [Fact]
    public void OneMinuteIsSingular()
    {
        Ages.Since(Now.AddMinutes(-1), Now).Should().Be("1 minute ago");
    }

    [Fact]
    public void MinutesAreCounted()
    {
        Ages.Since(Now.AddMinutes(-9), Now).Should().Be("9 minutes ago");
    }

    /// <summary>Rounded down, not up: 119 minutes is one hour ago and not two.</summary>
    [Fact]
    public void HoursRoundDown()
    {
        Ages.Since(Now.AddMinutes(-119), Now).Should().Be("1 hour ago");
    }

    [Fact]
    public void DaysAreTheCoarsestAnswer()
    {
        Ages.Since(Now.AddDays(-3), Now).Should().Be("3 days ago");
    }
}
