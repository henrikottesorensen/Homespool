using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Homespool.Host.Queue;
using Homespool.Host.Services;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="QueueSignal"/> - the poke that saves the advancer waiting out a tick.
/// </summary>
/// <remarks>
/// It carries no information and cannot fail, which is the whole design: everything the loop reacts to
/// is persisted, so a lost poke costs a tick's delay and nothing else. These pin the three properties
/// that make that true - it wakes, it coalesces, and it gives up on time when nobody pokes.
/// </remarks>
public class QueueSignalTests
{
    /// <summary>A poke ends the wait well before the interval would have.</summary>
    [Fact]
    public async Task APokeWakesAWaiterImmediately()
    {
        // Arrange
        using QueueSignal signal = new();
        Task waiting = signal.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Act
        long start = Stopwatch.GetTimestamp();
        signal.Poke();
        await waiting;

        // Assert
        Stopwatch.GetElapsedTime(start).Should().BeLessThan(TimeSpan.FromSeconds(5),
            "the poke exists precisely so somebody pressing Queue does not wait out a poll interval");
    }

    /// <summary>A poke with nobody waiting is not lost - the next wait returns at once.</summary>
    /// <remarks>
    /// The case that matters on a busy loop: enqueue happens while a pass is already running, so the
    /// poke lands between waits. Dropping it would delay that queue by a full interval for no reason.
    /// </remarks>
    [Fact]
    public async Task APokeWithNobodyWaitingIsRemembered()
    {
        // Arrange
        using QueueSignal signal = new();
        signal.Poke();

        // Act
        long start = Stopwatch.GetTimestamp();
        await signal.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Assert
        Stopwatch.GetElapsedTime(start).Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Many pokes between waits wake the loop once, not once each.
    /// </summary>
    /// <remarks>
    /// Coalescing rather than counting, because the loop re-reads everything anyway - there is no
    /// per-poke work to do. Ten queued files should cost one extra pass, not ten.
    /// </remarks>
    [Fact]
    public async Task ManyPokesCoalesceIntoOneWakeUp()
    {
        // Arrange
        using QueueSignal signal = new();

        for (int i = 0; i < 10; i++)
        {
            signal.Poke();
        }

        // Act - the first wait is satisfied by the pokes
        await signal.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        long start = Stopwatch.GetTimestamp();
        await signal.WaitAsync(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

        // Assert - the second waits out its interval, so the other nine did not queue up behind it
        Stopwatch.GetElapsedTime(start).Should().BeGreaterThan(TimeSpan.FromMilliseconds(100));
    }

    /// <summary>With nobody poking, the wait ends on its own - the timer is the mechanism.</summary>
    [Fact]
    public async Task AnUnpokedWaitEndsOnTheInterval()
    {
        // Arrange
        using QueueSignal signal = new();

        // Act
        long start = Stopwatch.GetTimestamp();
        await signal.WaitAsync(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

        // Assert
        Stopwatch.GetElapsedTime(start).Should().BeGreaterThan(TimeSpan.FromMilliseconds(100));
    }
}
