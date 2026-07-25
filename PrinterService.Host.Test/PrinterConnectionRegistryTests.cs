using System;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using NSubstitute;

using PrinterService.Host.PrusaConnect;

namespace PrinterService.Host.Test;

/// <summary>
/// <see cref="PrinterConnectionRegistry"/>, including the reconnect race a fast disconnect/reconnect
/// can trigger: a stale request's <c>finally</c>-block unregister must not delete a newer actor.
/// </summary>
public class PrinterConnectionRegistryTests
{
    /// <summary>An open actor that does nothing - these tests only care about registry bookkeeping,
    /// never about what reaches the wire.</summary>
    private static IPrinterConnectionActor OpenActor()
    {
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.IsOpen.Returns(true);

        return actor;
    }

    [Fact]
    public void RegisterThenTryGetReturnsTheSameActor()
    {
        // Arrange
        PrinterConnectionRegistry registry = new();
        IPrinterConnectionActor actor = OpenActor();

        // Act
        registry.Register(1, actor);

        // Assert
        registry.TryGet(1, out IPrinterConnectionActor? found).Should().BeTrue();
        found.Should().BeSameAs(actor);
    }

    [Fact]
    public void IsConnectedReflectsTheActorsIsOpenState()
    {
        // Arrange
        PrinterConnectionRegistry registry = new();
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.IsOpen.Returns(false);
        registry.Register(1, actor);

        // Act + Assert
        registry.IsConnected(1).Should().BeFalse();

        actor.IsOpen.Returns(true);
        registry.IsConnected(1).Should().BeTrue();
    }

    [Fact]
    public void UnregisteringAStaleActorDoesNotRemoveANewerOneForTheSamePrinter()
    {
        // Arrange
        PrinterConnectionRegistry registry = new();
        IPrinterConnectionActor actorA = OpenActor();
        IPrinterConnectionActor actorB = OpenActor();

        registry.Register(1, actorA);
        // Simulates a fast reconnect: a new connection registers its actor for the same printer
        // before the stale request's finally block runs its unregister.
        registry.Register(1, actorB);

        // Act
        registry.Unregister(1, actorA);

        // Assert
        registry.TryGet(1, out IPrinterConnectionActor? found).Should().BeTrue();
        found.Should().BeSameAs(actorB);
    }

    [Fact]
    public void UnregisteringTheCurrentActorRemovesIt()
    {
        // Arrange
        PrinterConnectionRegistry registry = new();
        IPrinterConnectionActor actor = OpenActor();
        registry.Register(1, actor);

        // Act
        registry.Unregister(1, actor);

        // Assert
        registry.TryGet(1, out _).Should().BeFalse();
        registry.IsConnected(1).Should().BeFalse();
    }
}
