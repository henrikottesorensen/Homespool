using System;
using System.Threading.Tasks;

using AwesomeAssertions;

using Homespool.Host.Queue;

namespace Homespool.Host.Test;

/// <summary>
/// What limits how many queue passes work at once - and why a pass waiting on a printer is not one
/// of them.
/// </summary>
public sealed class QueuePassWorkTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly QueueWorkBudget _budget = new(1);

    public void Dispose()
    {
        _budget.Dispose();
    }

    /// <summary>A budget needs a permit to give: none would stop every pass for good.</summary>
    [Fact]
    public void ABudgetOfNoPermitsIsRefused()
    {
        // Act
        Action create = () => _ = new QueueWorkBudget(0);

        // Assert
        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>With every permit taken, the next pass does not start until one is given back.</summary>
    [Fact]
    public async Task APassWaitsForAPermitUntilTheOneHoldingItEnds()
    {
        // Arrange
        using QueuePassWork first = new(_budget);
        using QueuePassWork second = new(_budget);
        await first.BeginAsync(TestContext.Current.CancellationToken);

        // Act
        Task begun = second.BeginAsync(TestContext.Current.CancellationToken);

        // Assert
        await Task.Delay(100, TestContext.Current.CancellationToken);
        begun.IsCompleted.Should().BeFalse("the budget's only permit is held");

        first.Dispose();
        await begun.WaitAsync(Bound, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A pass waiting on a printer holds nothing, so printers that have gone silent cannot between
    /// them stop every other printer's queue - and it takes a permit again to go on.
    /// </summary>
    [Fact]
    public async Task APassWaitingOnAPrinterHoldsNoPermitAndTakesOneAgain()
    {
        // Arrange
        using QueuePassWork waiting = new(_budget);
        using QueuePassWork other = new(_budget);
        await waiting.BeginAsync(TestContext.Current.CancellationToken);

        TaskCompletionSource<int> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act - the first pass waits on a printer, and another gets its turn meanwhile
        Task<int> wait = waiting.WhilePrinterAnswersAsync(() =>
        {
            asked.SetResult();

            return answer.Task;
        });
        await asked.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
        await other.BeginAsync(TestContext.Current.CancellationToken).WaitAsync(Bound, TestContext.Current.CancellationToken);

        answer.SetResult(7);

        // Assert - the answer is not handed on until the other pass has let go
        await Task.Delay(100, TestContext.Current.CancellationToken);
        wait.IsCompleted.Should().BeFalse("the pass has to take a permit again, and the other holds it");

        other.Dispose();
        (await wait.WaitAsync(Bound, TestContext.Current.CancellationToken)).Should().Be(7);

        _budget.Available.Should().Be(0, "the waiting pass holds its permit again");
        waiting.Dispose();
        _budget.Available.Should().Be(1);
    }

    /// <summary>
    /// What a wait throws is what the pass sees, and the pass holds its permit again when it does: a
    /// printer that did not answer is a verdict the pass goes on to act on, with the budget's
    /// accounting right.
    /// </summary>
    [Fact]
    public async Task AWaitThatThrowsStillThrowsAndTheClaimIsRestored()
    {
        // Arrange
        using QueuePassWork work = new(_budget);
        await work.BeginAsync(TestContext.Current.CancellationToken);

        // Act
        Func<Task> wait = () => work.WhilePrinterAnswersAsync<int>(() => throw new InvalidOperationException("no answer"));

        // Assert
        await wait.Should().ThrowAsync<InvalidOperationException>().WithMessage("no answer");
        _budget.Available.Should().Be(0, "the pass holds its permit again");

        work.Dispose();
        _budget.Available.Should().Be(1);
    }

    /// <summary>A pass that ends gives its permit back once - not twice, which would let one more in than the budget allows.</summary>
    [Fact]
    public async Task EndingAPassGivesItsPermitBackOnlyOnce()
    {
        // Arrange
        QueuePassWork work = new(_budget);
        await work.BeginAsync(TestContext.Current.CancellationToken);

        // Act
        work.Dispose();
        work.Dispose();

        // Assert
        _budget.Available.Should().Be(1);
    }

    /// <summary>A pass that never began has nothing to give back.</summary>
    [Fact]
    public void APassThatNeverBeganGivesBackNothing()
    {
        // Act
        new QueuePassWork(_budget).Dispose();

        // Assert
        _budget.Available.Should().Be(1);
    }
}
