using System;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

using NSubstitute;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrinterConnectionRegistry"/>, including the reconnect race a fast disconnect/reconnect
/// can trigger: a stale request's <c>finally</c>-block unregister must not delete a newer actor.
/// </summary>
public class PrinterConnectionRegistryTests
{
    private readonly FakeLogger<PrinterConnectionRegistry> _logger = new();

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
        PrinterConnectionRegistry registry = NewRegistry();
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
        PrinterConnectionRegistry registry = NewRegistry();
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
        PrinterConnectionRegistry registry = NewRegistry();
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
        PrinterConnectionRegistry registry = NewRegistry();
        IPrinterConnectionActor actor = OpenActor();
        registry.Register(1, actor);

        // Act
        registry.Unregister(1, actor);

        // Assert
        registry.TryGet(1, out _).Should().BeFalse();
        registry.IsConnected(1).Should().BeFalse();
    }

    /// <summary>
    /// A second connection for a printer that already has one takes over the command channel - the
    /// reconnect case this registry is built for - and the displaced connection is <em>shut down</em>
    /// rather than left running.
    /// </summary>
    /// <remarks>
    /// Before this, displacement was a silent overwrite: the loser's read loop kept persisting
    /// telemetry under the same printer id while being unreachable for commands, so one printer had
    /// two writers and a live state that flip-flopped between them. Found when two Buddy-rig clients
    /// accidentally ran against one identity (notes/buddy-rig.md).
    /// </remarks>
    [Fact]
    public void ASecondConnectionTakesOverAndShutsTheFirstOneDown()
    {
        // Arrange
        PrinterConnectionRegistry registry = NewRegistry();
        IPrinterConnectionActor first = OpenActor();
        IPrinterConnectionActor second = OpenActor();

        registry.Register(printerId: 1, first);

        // Act
        registry.Register(printerId: 1, second);

        // Assert
        registry.TryGet(1, out IPrinterConnectionActor? live).Should().BeTrue();
        live.Should().BeSameAs(second, "the newest connection owns the command channel");
        first.Received(1).Complete();
        second.DidNotReceive().Complete();
    }

    /// <summary>
    /// Displacement logs at Error, naming the printer - the only signal an operator can get, since a
    /// benign reconnect and someone replaying a stolen fingerprint/token are indistinguishable on
    /// the wire (both present valid credentials).
    /// </summary>
    [Fact]
    public void DisplacingAConnectionIsLoggedAtError()
    {
        // Arrange
        PrinterConnectionRegistry registry = NewRegistry();
        registry.Register(printerId: 7, OpenActor());

        // Act
        registry.Register(printerId: 7, OpenActor());

        // Assert
        FakeLogRecord record = _logger.Collector.GetSnapshot()
            .Should().ContainSingle(r => r.Level == LogLevel.Error).Subject;

        record.StructuredState.Should().Contain(kv => kv.Key == "PrinterId" && kv.Value == "7");
        record.Message.Should().Contain("compromised",
            "the message has to tell an operator what to do when it was not a reconnect");
    }

    /// <summary>A first registration is ordinary business and must stay silent.</summary>
    [Fact]
    public void AFirstConnectionLogsNothing()
    {
        // Arrange
        PrinterConnectionRegistry registry = NewRegistry();

        // Act
        registry.Register(printerId: 1, OpenActor());

        // Assert
        _logger.Collector.GetSnapshot().Should().BeEmpty();
    }

    private PrinterConnectionRegistry NewRegistry()
    {
        return new(_logger);
    }
}
