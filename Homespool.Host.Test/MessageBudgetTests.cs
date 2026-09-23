using System;
using System.Linq;

using AwesomeAssertions;

using Microsoft.Extensions.Time.Testing;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="MessageBudget"/>'s arithmetic: a burst free, then one message per refill interval,
/// and a quiet spell that never saves up more than the burst.
/// </summary>
public class MessageBudgetTests
{
    private readonly FakeTimeProvider _clock = new();

    [Fact]
    public void ABurstPassesWithoutWaitingAndTheNextMessageWaitsOneRefill()
    {
        // Arrange
        MessageBudget budget = new(perSecond: 5, burst: 3, _clock);

        // Act
        TimeSpan[] waits = Enumerable.Range(0, 4).Select(_ => budget.Take()).ToArray();

        // Assert
        waits.Should().Equal(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMilliseconds(200));
    }

    /// <summary>
    /// A sender that always waits what it is told settles at exactly the sustained rate - the debt
    /// is paid by the wait, not carried into the next message.
    /// </summary>
    [Fact]
    public void ASenderThatWaitsAsToldIsHeldToTheSustainedRate()
    {
        // Arrange
        MessageBudget budget = new(perSecond: 5, burst: 1, _clock);
        budget.Take();

        // Act
        TimeSpan[] waits = new TimeSpan[5];

        for (int i = 0; i < waits.Length; i++)
        {
            waits[i] = budget.Take();
            _clock.Advance(waits[i]);
        }

        // Assert
        waits.Should().AllBeEquivalentTo(TimeSpan.FromMilliseconds(200));
    }

    /// <summary>
    /// Messages taken without waiting stack their debt, so a sender cannot get ahead by ignoring one
    /// answer and asking again.
    /// </summary>
    [Fact]
    public void DebtAccumulatesWhenNothingWaits()
    {
        // Arrange
        MessageBudget budget = new(perSecond: 5, burst: 1, _clock);
        budget.Take();

        // Act
        TimeSpan first = budget.Take();
        TimeSpan second = budget.Take();

        // Assert
        first.Should().Be(TimeSpan.FromMilliseconds(200));
        second.Should().Be(TimeSpan.FromMilliseconds(400));
    }

    [Fact]
    public void AQuietSpellRefillsTheBurstAndNoMore()
    {
        // Arrange
        MessageBudget budget = new(perSecond: 5, burst: 2, _clock);
        budget.Take();
        budget.Take();

        // Act
        _clock.Advance(TimeSpan.FromHours(1));
        TimeSpan[] waits = Enumerable.Range(0, 3).Select(_ => budget.Take()).ToArray();

        // Assert
        waits.Should().Equal(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMilliseconds(200));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    public void ABudgetThatAdmitsNothingIsRefused(int perSecond, int burst)
    {
        // Act
        Action create = () => _ = new MessageBudget(perSecond, burst, _clock);

        // Assert
        create.Should().Throw<ArgumentOutOfRangeException>();
    }
}
