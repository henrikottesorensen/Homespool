using System;

using AwesomeAssertions;

using Microsoft.Extensions.Time.Testing;

using Homespool.Host.Authentication;

namespace Homespool.Host.Test;

/// <summary>
/// The bucket on its own: what it starts with, how it refills, where it stops, and how often it says
/// it is empty.
/// </summary>
public sealed class ProvisioningHashBudgetTests
{
    /// <summary>Stored credentials, as the database hands them over; only their identity matters here.</summary>
    private const string Credential = "$SHA384$4096$c2FsdA$aGFzaA$";

    private const string OtherCredential = "$SHA384$4096$c2FsdDI$aGFzaDI$";

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    private static void Drain(ProvisioningHashBudget budget)
    {
        while (budget.TryTake())
        {
        }
    }

    [Fact]
    public void AFullBucketGivesTheBurstAndNoMore()
    {
        // Arrange
        ProvisioningHashBudget budget = new(_time);
        int taken = 0;

        // Act
        while (budget.TryTake())
        {
            taken++;
        }

        // Assert
        taken.Should().Be(ProvisioningHashBudget.Burst);
    }

    [Fact]
    public void ASecondRefillsTheRate()
    {
        // Arrange
        ProvisioningHashBudget budget = new(_time);
        Drain(budget);

        // Act
        _time.Advance(TimeSpan.FromSeconds(1));

        // Assert
        budget.Available.Should().Be((int)ProvisioningHashBudget.HashesPerSecond);
    }

    [Fact]
    public void PartOfASecondRefillsItsShare()
    {
        // Arrange
        ProvisioningHashBudget budget = new(_time);
        Drain(budget);

        // Act
        _time.Advance(TimeSpan.FromMilliseconds(250));

        // Assert
        budget.Available.Should().Be(2);
    }

    [Fact]
    public void RefillingNeverExceedsTheBurst()
    {
        // Arrange
        ProvisioningHashBudget budget = new(_time);
        Drain(budget);

        // Act
        _time.Advance(TimeSpan.FromHours(1));

        // Assert
        budget.Available.Should().Be(ProvisioningHashBudget.Burst);
    }

    [Fact]
    public void NothingIsReportedWhileNothingIsRefused()
    {
        // Arrange
        ProvisioningHashBudget budget = new(_time);
        budget.TryTake();

        // Act
        long? report = budget.TakeReport();

        // Assert
        report.Should().BeNull();
    }

    /// <summary>
    /// The first refusal is reported with everything refused so far, the next is held back for an
    /// interval, and the one after that carries only what was refused since.
    /// </summary>
    [Fact]
    public void AnEmptyBucketIsReportedAtMostOnceAnInterval()
    {
        // Arrange
        ProvisioningHashBudget budget = new(_time);
        Drain(budget);
        budget.TryTake();
        budget.TryTake();

        // Act
        long? first = budget.TakeReport();

        budget.TryTake();
        long? held = budget.TakeReport();

        _time.Advance(ProvisioningHashBudget.ReportInterval);
        long? next = budget.TakeReport();

        // Assert
        first.Should().Be(3, "the take that found the bucket empty while draining counts too");
        held.Should().BeNull();
        next.Should().Be(1);
    }

    // ---------- enrolled credentials ----------
    [Fact]
    public void ACredentialsFirstHashIsFree()
    {
        // Arrange
        ProvisioningHashBudget budget = new(_time);

        // Act
        bool taken = budget.TryTakeFor(Credential);

        // Assert
        taken.Should().BeTrue();
        budget.Available.Should().Be(ProvisioningHashBudget.Burst);
    }

    [Fact]
    public void ACredentialsSecondHashInAnIntervalDrawsFromTheBucket()
    {
        // Arrange
        ProvisioningHashBudget budget = new(_time);
        budget.TryTakeFor(Credential);

        // Act
        bool taken = budget.TryTakeFor(Credential);

        // Assert
        taken.Should().BeTrue();
        budget.Available.Should().Be(ProvisioningHashBudget.Burst - 1);
    }

    [Fact]
    public void ACredentialsFreeHashReturnsAfterAnInterval()
    {
        // Arrange
        ProvisioningHashBudget budget = new(_time);
        budget.TryTakeFor(Credential);
        Drain(budget);

        // Act
        _time.Advance(ProvisioningHashBudget.CredentialInterval);
        Drain(budget);
        bool taken = budget.TryTakeFor(Credential);

        // Assert
        taken.Should().BeTrue();
    }

    [Fact]
    public void EachCredentialHasItsOwnFreeHash()
    {
        // Arrange
        ProvisioningHashBudget budget = new(_time);
        budget.TryTakeFor(Credential);
        Drain(budget);

        // Act
        bool taken = budget.TryTakeFor(OtherCredential);

        // Assert
        taken.Should().BeTrue();
    }

    [Fact]
    public void ASpentCredentialAgainstAnEmptyBucketIsRefused()
    {
        // Arrange
        ProvisioningHashBudget budget = new(_time);
        budget.TryTakeFor(Credential);
        Drain(budget);

        // Act
        bool taken = budget.TryTakeFor(Credential);

        // Assert
        taken.Should().BeFalse();
    }
}
