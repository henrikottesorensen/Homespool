using AwesomeAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

using NSubstitute;

using Homespool.Host.Printing;
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
        registry.Register(1, actor, overPlaintext: false);

        // Assert
        registry.TryGet(1, out IPrinterLink? found).Should().BeTrue();
        found.Should().BeSameAs(actor);
    }

    [Fact]
    public void IsConnectedReflectsTheActorsIsOpenState()
    {
        // Arrange
        PrinterConnectionRegistry registry = NewRegistry();
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.IsOpen.Returns(false);
        registry.Register(1, actor, overPlaintext: false);

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

        registry.Register(1, actorA, overPlaintext: false);

        // Simulates a fast reconnect: a new connection registers its actor for the same printer
        // before the stale request's finally block runs its unregister.
        registry.Register(1, actorB, overPlaintext: false);

        // Act
        registry.Unregister(1, actorA);

        // Assert
        registry.TryGet(1, out IPrinterLink? found).Should().BeTrue();
        found.Should().BeSameAs(actorB);
    }

    [Fact]
    public void UnregisteringTheCurrentActorRemovesIt()
    {
        // Arrange
        PrinterConnectionRegistry registry = NewRegistry();
        IPrinterConnectionActor actor = OpenActor();
        registry.Register(1, actor, overPlaintext: false);

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
    /// accidentally ran against one identity.
    /// </remarks>
    [Fact]
    public void ASecondConnectionTakesOverAndShutsTheFirstOneDown()
    {
        // Arrange
        PrinterConnectionRegistry registry = NewRegistry();
        IPrinterConnectionActor first = OpenActor();
        IPrinterConnectionActor second = OpenActor();

        registry.Register(printerId: 1, first, overPlaintext: false);

        // Act
        registry.Register(printerId: 1, second, overPlaintext: false);

        // Assert
        registry.TryGet(1, out IPrinterLink? live).Should().BeTrue();
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
        registry.Register(printerId: 7, OpenActor(), overPlaintext: false);

        // Act
        registry.Register(printerId: 7, OpenActor(), overPlaintext: false);

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
        registry.Register(printerId: 1, OpenActor(), overPlaintext: false);

        // Assert
        _logger.Collector.GetSnapshot().Should().BeEmpty();
    }

    /// <summary>
    /// Closing shuts the live connection down, so a printer being deleted stops producing rows that
    /// would reference it.
    /// </summary>
    [Fact]
    public void CloseCompletesTheRegisteredActor()
    {
        // Arrange
        PrinterConnectionRegistry registry = NewRegistry();
        IPrinterConnectionActor actor = OpenActor();
        registry.Register(printerId: 1, actor, overPlaintext: false);

        // Act
        bool closed = registry.Close(printerId: 1);

        // Assert
        closed.Should().BeTrue();
        actor.Received(1).Complete();
    }

    /// <summary>
    /// A printer that was never connected is deleted just as often as one that was, so closing has
    /// to be a no-op rather than a failure.
    /// </summary>
    [Fact]
    public void ClosingAPrinterThatIsNotConnectedReportsSo()
    {
        // Arrange
        PrinterConnectionRegistry registry = NewRegistry();

        // Act + Assert
        registry.Close(printerId: 1).Should().BeFalse();
    }

    /// <summary>
    /// <b>Closing does not remove the entry</b>, which is what keeps it clear of the reconnect race
    /// above: the teardown that follows <c>Complete</c> unregisters by instance, and removing it here
    /// as well would take out whatever registered in between.
    /// </summary>
    [Fact]
    public void CloseLeavesTheEntryForTheTeardownToUnregisterByInstance()
    {
        // Arrange
        PrinterConnectionRegistry registry = NewRegistry();
        IPrinterConnectionActor closed = OpenActor();
        registry.Register(printerId: 1, closed, overPlaintext: false);

        registry.Close(printerId: 1);

        // Act - a reconnect lands before the closed connection's request has torn down, then the
        // stale request finally runs its unregister.
        IPrinterConnectionActor reconnected = OpenActor();
        registry.Register(printerId: 1, reconnected, overPlaintext: false);
        registry.Unregister(printerId: 1, closed);

        // Assert
        registry.TryGet(1, out IPrinterLink? found).Should().BeTrue();
        found.Should().BeSameAs(reconnected);
    }

    private PrinterConnectionRegistry NewRegistry()
    {
        return new(_logger);
    }

    /// <summary>
    /// The listener a connection arrived on is carried, because a warning about it has nowhere else
    /// to read it from - nothing persists which door a printer uses.
    /// </summary>
    [Fact]
    public void APrinterRegisteredOnThePlaintextListenerIsReportedAsSuch()
    {
        PrinterConnectionRegistry registry = NewRegistry();
        IPrinterLink actor = Substitute.For<IPrinterLink>();
        actor.IsOpen.Returns(true);

        registry.Register(1, actor, overPlaintext: true);

        registry.IsOnPlaintextListener(1).Should().BeTrue();
        registry.PrintersOnPlaintextListener().Should().Equal(1);
    }

    [Fact]
    public void APrinterOnTheTlsListenerIsNotReportedAsPlaintext()
    {
        PrinterConnectionRegistry registry = NewRegistry();
        IPrinterLink actor = Substitute.For<IPrinterLink>();
        actor.IsOpen.Returns(true);

        registry.Register(1, actor, overPlaintext: false);

        registry.IsOnPlaintextListener(1).Should().BeFalse();
        registry.PrintersOnPlaintextListener().Should().BeEmpty();
    }

    /// <summary>
    /// <b>A printer nobody is connected to is unknown, not protected.</b> Which listener it would use
    /// lives in the ini on its own stick, so an absent connection answers nothing - and false here
    /// must be read as "no warning to give", never as "this printer is fine".
    /// </summary>
    [Fact]
    public void ADisconnectedPrinterReportsNoPlaintextConnection()
    {
        PrinterConnectionRegistry registry = NewRegistry();
        IPrinterLink actor = Substitute.For<IPrinterLink>();
        actor.IsOpen.Returns(false);

        registry.Register(1, actor, overPlaintext: true);

        registry.IsOnPlaintextListener(1).Should().BeFalse();
        registry.IsOnPlaintextListener(2).Should().BeFalse("printer 2 has never connected at all");
    }

    /// <summary>
    /// A reconnect onto the other listener replaces the answer rather than adding to it - which is
    /// what makes re-provisioning onto TLS clear the warning by itself.
    /// </summary>
    [Fact]
    public void ReconnectingOnTheTlsListenerClearsThePlaintextAnswer()
    {
        PrinterConnectionRegistry registry = NewRegistry();
        IPrinterLink onPlaintext = Substitute.For<IPrinterLink>();
        onPlaintext.IsOpen.Returns(true);
        IPrinterLink onTls = Substitute.For<IPrinterLink>();
        onTls.IsOpen.Returns(true);

        registry.Register(1, onPlaintext, overPlaintext: true);
        registry.Register(1, onTls, overPlaintext: false);

        registry.IsOnPlaintextListener(1).Should().BeFalse();
    }

    /// <summary>
    /// Unregister still matches on the link instance, which the stored value must not have broken:
    /// a fast reconnect registers the newcomer before the old request's finally runs.
    /// </summary>
    [Fact]
    public void UnregisteringAStaleLinkLeavesTheOneThatDisplacedIt()
    {
        PrinterConnectionRegistry registry = NewRegistry();
        IPrinterLink stale = Substitute.For<IPrinterLink>();
        stale.IsOpen.Returns(true);
        IPrinterLink live = Substitute.For<IPrinterLink>();
        live.IsOpen.Returns(true);

        registry.Register(1, stale, overPlaintext: true);
        registry.Register(1, live, overPlaintext: false);
        registry.Unregister(1, stale);

        registry.TryGet(1, out IPrinterLink? found).Should().BeTrue();
        found.Should().BeSameAs(live);
    }
}
