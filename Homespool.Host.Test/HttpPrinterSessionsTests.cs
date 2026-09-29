using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Transfers;
using Homespool.Host.Queue;
using Homespool.Host.Telemetry;

namespace Homespool.Host.Test;

/// <summary>
/// A printer on the HTTP transport keeps one session across its posts, and loses it to a connection
/// that registers under the same printer - but only until its next post, which takes the identity back.
/// </summary>
/// <remarks>
/// The registry is real, because what these pin is the agreement between it and the sessions: the
/// registry decides who holds a printer, and a session whose actor it no longer holds is not reused.
/// </remarks>
public sealed class HttpPrinterSessionsTests : IDisposable
{
    private const int PrinterId = 7;

    private readonly PrinterConnectionRegistry _registry = new(NullLogger<PrinterConnectionRegistry>.Instance);
    private readonly RecordingActorFactory _actors = new();
    private readonly FakeLogger<HttpPrinterSessions> _logger = new();
    private readonly FakeTimeProvider _time = new();
    private readonly QueueSignal _queueSignal = new();

    public void Dispose()
    {
        _queueSignal.Dispose();
    }

    [Fact]
    public void ASessionStillRegisteredIsReused()
    {
        // Arrange
        using HttpPrinterSessions sessions = Build();

        // Act
        IPrinterConnectionActor first = sessions.GetOrCreate(PrinterId, overPlaintext: false);
        IPrinterConnectionActor second = sessions.GetOrCreate(PrinterId, overPlaintext: false);

        // Assert
        second.Should().BeSameAs(first, "a printer's posts share one session while nothing has displaced it");
        _actors.Created.Should().ContainSingle();
    }

    /// <summary>
    /// Another connection registering under the printer completes the session's actor; the next post
    /// gets a fresh one, which displaces that connection in its turn.
    /// </summary>
    /// <remarks>
    /// Handed the completed actor instead, the post would fail against a closed mailbox - and keep the
    /// session looking busy, so the reaper would never replace it and the displacing connection would
    /// keep the printer for as long as it stayed.
    /// </remarks>
    [Fact]
    public async Task ADisplacedSessionIsReplacedOnTheNextPost()
    {
        // Arrange
        using HttpPrinterSessions sessions = Build();

        IPrinterConnectionActor displaced = sessions.GetOrCreate(PrinterId, overPlaintext: false);

        IPrinterLink socket = Substitute.For<IPrinterLink>();
        _registry.Register(PrinterId, socket, overPlaintext: false);

        displaced.Received(1).Complete();

        // Act
        IPrinterConnectionActor replacement = sessions.GetOrCreate(PrinterId, overPlaintext: false);

        // Assert
        replacement.Should().NotBeSameAs(displaced);

        _registry.TryGet(PrinterId, out IPrinterLink? live).Should().BeTrue();
        live.Should().BeSameAs(replacement, "the printer's own post takes its identity back");

        socket.Received(1).Complete();

        // The set-aside session is torn down by the reaper, not left behind.
        await sessions.StopAsync(CancellationToken.None);

        Reasons().Should().BeEquivalentTo(["displaced", "shutting down"]);
    }

    /// <summary>
    /// A displaced session nobody posts to again is reaped as displaced, rather than waiting out the
    /// idle window and going as idle.
    /// </summary>
    /// <remarks>
    /// The reaper's loop starts on the pool, so its timer may not exist yet when time first moves;
    /// time is moved in steps until something is torn down, and the reason is what is asserted.
    /// </remarks>
    [Fact]
    public async Task ADisplacedSessionIsReapedAsDisplaced()
    {
        // Arrange
        using HttpPrinterSessions sessions = Build();

        await sessions.StartAsync(CancellationToken.None);

        sessions.GetOrCreate(PrinterId, overPlaintext: false);

        _registry.Register(PrinterId, Substitute.For<IPrinterLink>(), overPlaintext: false);

        // Act
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        while (Reasons().Count == 0)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10, timeout.Token);
        }

        // Assert
        Reasons().Should().Equal("displaced");

        await sessions.StopAsync(CancellationToken.None);
    }

    private HttpPrinterSessions Build()
    {
        return new HttpPrinterSessions(_registry, _actors, _queueSignal, _time, _logger);
    }

    /// <summary>The reason on every teardown line, in the order they were logged.</summary>
    private List<string?> Reasons()
    {
        return _logger.Collector.GetSnapshot()
                      .Where(record => record.Level == LogLevel.Information &&
                                       record.Message.Contains("disconnected from the HTTP transport", StringComparison.Ordinal))
                      .Select(record => record.StructuredState!.Single(pair => pair.Key == "Reason").Value)
                      .ToList();
    }

    /// <summary>Hands back a distinct, already-drained actor per session, and keeps them.</summary>
    private sealed class RecordingActorFactory()
        : PrinterConnectionActorFactory(
            Substitute.For<ITelemetrySink>(),
            NullLogger<PrinterConnectionActor>.Instance,
            TestOptions.Monitor(new PrusaConnectOptions()),
            Substitute.For<ITransferContentStore>(),
            PrinterTrafficLogTests.Off)
    {
        public List<IPrinterConnectionActor> Created { get; } = [];

        public override IPrinterConnectionActor Create(int printerId, IPrinterConnection connection)
        {
            IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
            actor.Completion.Returns(Task.CompletedTask);

            Created.Add(actor);

            return actor;
        }
    }
}
