using System;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

using NSubstitute;

using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Transfers;
using Homespool.Host.Queue;
using Homespool.Host.Telemetry;

namespace Homespool.Host.Test;

/// <summary>
/// What a printer calls itself reaches a log line cleaned and bounded, at the description and at the
/// two lines of <see cref="HttpPrinterSessions"/> that carry it.
/// </summary>
/// <remarks>
/// <c>LogTextTests</c> pins what cleaning means; this pins that these sites call it. An enrolled
/// printer chooses its <c>User-Agent</c> byte for byte, and being enrolled says who it is rather than
/// what it sends. The escapes are written rather than pasted, for the reason given there.
/// </remarks>
public sealed class PrinterClientLogTests : IDisposable
{
    private const int PrinterId = 7;

    private const string DirtyAgent = "curl\u001B[2J\u202Eagent";

    private const string CleanedAgent = "curl\uFFFD[2J\uFFFDagent";

    private readonly FakeLogger<HttpPrinterSessions> _logger = new();
    private readonly QueueSignal _queueSignal = new();

    public void Dispose()
    {
        _queueSignal.Dispose();
    }

    /// <summary>The description is the log form, so it is where both of the printer's strings are cleaned.</summary>
    [Fact]
    public void TheDescriptionCleansTheAgentAndTheFirmwareVersion()
    {
        PrinterClient client = new(PrinterTransport.Http, DirtyAgent, "6.4.0\n+forged");

        client.Describe.Should().Be($"Http, {CleanedAgent}, firmware 6.4.0\uFFFD+forged");
    }

    /// <summary>A header runs to kilobytes; the line keeps the start and says how much arrived.</summary>
    [Fact]
    public void TheDescriptionBoundsAnOverLongAgent()
    {
        PrinterClient client = new(PrinterTransport.Http, new string('x', 5000));

        client.Describe.Should().Be($"Http, {new string('x', PrinterClient.MaxLoggedLength)}<5000 characters in all>");
    }

    /// <summary>
    /// Recognition still reads what was really sent: cleaning is for the reader of a log, and a
    /// comparison made against the cleaned form would be a comparison against something nobody said.
    /// </summary>
    [Fact]
    public void TheAgentItselfIsKeptAsItArrived()
    {
        PrinterClient client = new(PrinterTransport.Http, DirtyAgent);

        client.UserAgent.Should().Be(DirtyAgent);
        client.UserAgentForLog.Should().Be(CleanedAgent);
    }

    /// <summary>Both lines a first request writes about the client: the connect line, and the louder one.</summary>
    [Fact]
    public void AnHttpPrinterIsAnnouncedInTheLogCleaned()
    {
        // Arrange
        using HttpPrinterSessions sessions = new(new PrinterConnectionRegistry(NullLogger<PrinterConnectionRegistry>.Instance),
                                                 new StubActorFactory(),
                                                 _queueSignal,
                                                 TimeProvider.System,
                                                 _logger);

        // Act
        sessions.GetOrCreate(PrinterId, overPlaintext: false, DirtyAgent);

        // Assert
        FakeLogRecord connected = _logger.Collector.GetSnapshot().Should()
                                         .ContainSingle(record => record.Level == LogLevel.Information)
                                         .Subject;

        connected.StructuredState.Should().Contain(pair => pair.Key == "Client" && pair.Value == $"Http, {CleanedAgent}");

        FakeLogRecord unrecognised = _logger.Collector.GetSnapshot().Should()
                                            .ContainSingle(record => record.Level == LogLevel.Warning)
                                            .Subject;

        unrecognised.StructuredState.Should().Contain(pair => pair.Key == "UserAgent" && pair.Value == CleanedAgent);

        _logger.Collector.GetSnapshot()
               .SelectMany(record => record.StructuredState!)
               .Should().NotContain(pair => pair.Value != null && pair.Value.Contains('\u001B'));
    }

    /// <summary>Hands back an actor that has already drained, so no socket is needed to build a session.</summary>
    private sealed class StubActorFactory()
        : PrinterConnectionActorFactory(
            Substitute.For<ITelemetrySink>(),
            NullLogger<PrinterConnectionActor>.Instance,
            TestOptions.Monitor(new PrusaConnectOptions()),
            Substitute.For<ITransferContentStore>(),
            PrinterTrafficLogTests.Off)
    {
        public override IPrinterConnectionActor Create(int printerId, IPrinterConnection connection)
        {
            IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
            actor.Completion.Returns(Task.CompletedTask);

            return actor;
        }
    }
}
