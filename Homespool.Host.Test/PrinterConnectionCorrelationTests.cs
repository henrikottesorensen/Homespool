using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

using NSubstitute;

using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.PrusaConnect.Transfers;
using Homespool.Host.Queue;
using Homespool.Host.Telemetry;

namespace Homespool.Host.Test;

/// <summary>
/// The logging correlation <see cref="PrinterConnectionSession"/> opens around a connection -
/// <c>PrinterId</c> as a structured property rather than message text, and a <c>ConnectionId</c>
/// that tells one printer's reconnects apart.
/// </summary>
/// <remarks>
/// Separate from <see cref="PrinterConnectionSessionTests"/>, which is about the teardown sequence.
/// The case worth having here is <see cref="TheActorsOwnLoggingInheritsTheCorrelation"/>: the actor
/// starts its loop in its <i>constructor</i>, so a scope opened even one line too late leaves every
/// actor log line uncorrelated - and nothing fails, the log just quietly stops answering the
/// question it was added for.
/// </remarks>
public sealed class PrinterConnectionCorrelationTests : IDisposable
{
    private const int PrinterId = 7;

    /// <summary>
    /// Shared and never observed: the session pokes it when a printer registers, and nothing here
    /// cares. Static because it is a process-wide singleton in production too, which also keeps four
    /// test classes from growing disposal ceremony for a semaphore that outlives them all.
    /// </summary>
    private static readonly QueueSignal QueueSignal = new();

    private readonly FakeLogCollector _collector = new();

    private readonly ILoggerFactory _factory;

    public PrinterConnectionCorrelationTests()
    {
        // One factory, one collector, several categories - so a scope opened on the session's logger
        // is observable on records written through a different logger, which is the whole point.
        _factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(new FakeLoggerProvider(_collector));
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private IReadOnlyList<FakeLogRecord> Records => _collector.GetSnapshot();

    private static IPrinterConnectionActor DrainedActor()
    {
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.Completion.Returns(Task.CompletedTask);

        return actor;
    }

    private static IReadOnlyDictionary<string, object?> ScopeOf(FakeLogRecord record)
    {
        return record.Scopes
                     .OfType<IEnumerable<KeyValuePair<string, object>>>()
                     .SelectMany(scope => scope)
                     .ToDictionary(pair => pair.Key, pair => (object?)pair.Value);
    }

    private async Task RunSessionAsync(PrinterConnectionActorFactory actorFactory)
    {
        PrinterConnectionSession session = new(
            new StubHandler(_factory.CreateLogger<WebSocketHandler>()),
            new PrinterConnectionRegistry(NullLogger<PrinterConnectionRegistry>.Instance),
            actorFactory,
            QueueSignal,
            _factory.CreateLogger<PrinterConnectionSession>())
        {
            ActorDrainTimeout = TimeSpan.FromMilliseconds(150),
        };

        Pipe wire = new();

        await session.RunAsync(PrinterId, new NullConnection(), wire.Reader, CancellationToken.None);
    }

    [Fact]
    public async Task EveryLineFromTheReadLoopCarriesThePrinterAndConnectionId()
    {
        // Act
        await RunSessionAsync(new StubActorFactory(DrainedActor(), logger: null));

        // Assert
        FakeLogRecord fromReadLoop = Records.Should()
                                            .Contain(record => record.Category!.Contains(
                                                         "WebSocketHandler", StringComparison.Ordinal)).Subject;

        ScopeOf(fromReadLoop).Should().Contain("PrinterId", PrinterId);
        ScopeOf(fromReadLoop).Should().ContainKey("ConnectionId");
    }

    /// <summary>
    /// The ordering guard. <c>PrinterConnectionActor</c>'s constructor starts its own loop, and a
    /// logging scope is <c>AsyncLocal</c> - captured when a task starts. So the scope must be open
    /// <i>before</i> the factory is called, and this fails if it is ever moved below.
    /// </summary>
    [Fact]
    public async Task TheActorsOwnLoggingInheritsTheCorrelation()
    {
        // Arrange - a factory that logs from inside Create, standing in for the real actor's
        // constructor-started loop
        StubActorFactory factory = new(DrainedActor(), _factory.CreateLogger<PrinterConnectionActor>());

        // Act
        await RunSessionAsync(factory);

        // Assert
        FakeLogRecord atActorCreation = Records.Should()
                                               .Contain(record => record.Category!.Contains(
                                                            "PrinterConnectionActor", StringComparison.Ordinal)).Subject;

        ScopeOf(atActorCreation).Should().Contain("PrinterId", PrinterId);
        ScopeOf(atActorCreation).Should().ContainKey("ConnectionId");
    }

    [Fact]
    public async Task EachConnectionGetsItsOwnConnectionId()
    {
        // Act - the same printer connecting twice, as a flapping network produces
        await RunSessionAsync(new StubActorFactory(DrainedActor(), logger: null));
        await RunSessionAsync(new StubActorFactory(DrainedActor(), logger: null));

        // Assert
        object?[] connectionIds = Records
                                  .Select(ScopeOf)
                                  .Where(scope => scope.ContainsKey("ConnectionId"))
                                  .Select(scope => scope["ConnectionId"])
                                  .Distinct()
                                  .ToArray();

        connectionIds.Should().HaveCount(2, "a printer id alone cannot tell two connections apart");
    }

    /// <summary>Ends the read loop immediately, after one log line to observe the scope through.</summary>
    private sealed class StubHandler(ILogger logger) : WebSocketHandler(
        NullLogger<WebSocketHandler>.Instance,
        new MessageDispatcher(NullLogger<MessageDispatcher>.Instance,
                              new UnknownFieldTracker(NullLogger<UnknownFieldTracker>.Instance),
                              TimeProvider.System),
        TestOptions.Monitor(new PrusaConnectOptions()))
    {
        public override Task HandlePrusaWebsocket(PipeReader input,
                                                  int printerId,
                                                  IPrinterConnectionActor actor,
                                                  CancellationToken cancellationToken)
        {
            logger.LogInformation("read loop running");

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Hands back a prepared actor, optionally logging as it does - which is how the constructor's
    /// own logging is stood in for without a socket to build a real actor over.
    /// </summary>
    private sealed class StubActorFactory(IPrinterConnectionActor actor, ILogger? logger)
        : PrinterConnectionActorFactory(
            Substitute.For<ITelemetrySink>(),
            NullLogger<PrinterConnectionActor>.Instance,
            TestOptions.Monitor(new PrusaConnectOptions()),
            Substitute.For<ITransferContentStore>())
    {
        public override IPrinterConnectionActor Create(int printerId, IPrinterConnection connection)
        {
            logger?.LogInformation("actor constructed");

            return actor;
        }
    }

    /// <summary>A connection that accepts the close and nothing else.</summary>
    private sealed class NullConnection : IClosablePrinterConnection
    {
        public bool IsOpen => true;

        public ValueTask<CommandHandover> SendCommandAsync(uint commandId, ISendableCommand command, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(CommandHandover.Written);
        }

        public PendingCommand? TakeParkedCommand()
        {
            return null;
        }

        public Task CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus status)
        {
            return Task.CompletedTask;
        }
    }
}
