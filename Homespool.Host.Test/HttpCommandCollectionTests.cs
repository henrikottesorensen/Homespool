using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using AwesomeAssertions;

using NSubstitute;

using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;

namespace Homespool.Host.Test;

/// <summary>
/// What a poll sends once the actor has answered it: a command goes out only when the loop has
/// released it, and a connection torn down at any step sends nothing.
/// </summary>
/// <remarks>
/// The actor is a substitute because the cases are orderings a real loop cannot be stopped between -
/// the take answered, then the mailbox closed before the delivery is posted. What the loop itself
/// answers while draining is in <see cref="PrinterConnectionActorTests"/>.
/// </remarks>
public sealed class HttpCommandCollectionTests
{
    private static readonly PendingCommand Parked = new(42, new PausePrint());

    [Fact]
    public async Task ACommandTheLoopReleasesIsSent()
    {
        // Arrange
        IPrinterConnectionActor actor = ActorAnswering(Parked, released: true);

        // Act
        PendingCommand? collected = await Eventually(HttpCommandCollection.CollectAsync(actor, CancellationToken.None));

        // Assert
        collected.Should().BeSameAs(Parked);
    }

    [Fact]
    public async Task ACommandTheLoopKeepsBackIsNotSent()
    {
        // Arrange
        IPrinterConnectionActor actor = ActorAnswering(Parked, released: false);

        // Act
        PendingCommand? collected = await Eventually(HttpCommandCollection.CollectAsync(actor, CancellationToken.None));

        // Assert
        collected.Should().BeNull("the loop was draining, and its teardown tells the caller the command never left");
    }

    [Fact]
    public async Task ACommandWhoseDeliveryFindsTheMailboxClosedIsNotSent()
    {
        // Arrange
        IPrinterConnectionActor actor = ActorAnswering(Parked, released: true, closedTo: message => message is CommandDeliveredMessage);

        // Act
        PendingCommand? collected = await Eventually(HttpCommandCollection.CollectAsync(actor, CancellationToken.None));

        // Assert
        collected.Should().BeNull("the teardown began between the take and the delivery");
    }

    [Fact]
    public async Task APollThatFindsTheMailboxClosedCollectsNothing()
    {
        // Arrange
        IPrinterConnectionActor actor = ActorAnswering(Parked, released: true, closedTo: message => message is TakePendingCommandMessage);

        // Act
        PendingCommand? collected = await Eventually(HttpCommandCollection.CollectAsync(actor, CancellationToken.None));

        // Assert
        collected.Should().BeNull("nothing was taken, and the teardown settles whatever is parked");
    }

    /// <summary>
    /// A ceiling on a collect, so a regression that leaves one waiting fails rather than hangs.
    /// </summary>
    private static Task<T> Eventually<T>(Task<T> task)
    {
        return task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// An actor whose loop answers a take with <paramref name="parked"/> and a delivery with
    /// <paramref name="released"/>, and whose mailbox is closed to whatever <paramref name="closedTo"/>
    /// picks out.
    /// </summary>
    [SuppressMessage("Reliability", "CA2012:Use ValueTasks correctly",
                     Justification = "NSubstitute call specification, not an invocation.")]
    private static IPrinterConnectionActor ActorAnswering(PendingCommand parked,
                                                          bool released,
                                                          Func<ConnectionMessage, bool>? closedTo = null)
    {
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();

        actor.PostAsync(default!, default)
             .ReturnsForAnyArgs(call =>
             {
                 ConnectionMessage message = call.Arg<ConnectionMessage>();

                 if (closedTo?.Invoke(message) == true)
                 {
                     return ValueTask.FromException(new ChannelClosedException());
                 }

                 switch (message)
                 {
                     case TakePendingCommandMessage take:
                         take.Completion.TrySetResult(parked);
                         break;

                     case CommandDeliveredMessage delivered:
                         delivered.Completion.TrySetResult(released);
                         break;
                 }

                 return ValueTask.CompletedTask;
             });

        return actor;
    }
}
