using System;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="MessageDispatcher.Classify(int, JsonElement)"/> - one parsed wire message in, one typed
/// <see cref="ConnectionMessage"/> out. Pure classification: what the actor <i>does</i> with each
/// message (correlation, the sink) is covered by <see cref="PrinterConnectionActorTests"/>.
/// </summary>
public class MessageDispatcherTests
{
    private static MessageDispatcher NewDispatcher()
    {
        return new(NullLogger<MessageDispatcher>.Instance, NewTracker(), TimeProvider.System,
                   PrinterTrafficLogTests.Off);
    }

    private static UnknownFieldTracker NewTracker()
    {
        return new(NullLogger<UnknownFieldTracker>.Instance);
    }

    /// <summary>
    /// The <c>InlineRequest</c> shape from firmware's <c>render.cpp:100-119</c>
    /// (<c>transfers::Download::InlineRequest</c>) - the printer asking for the next chunk of a
    /// Connect-initiated file upload. No captured message of this shape exists, so this is built
    /// from the documented firmware source rather than replayed. Has neither <c>"event"</c> nor
    /// <c>"state"</c>, which is exactly what
    /// would previously have mis-routed it into the telemetry branch.
    /// </summary>
    private const string InlineTransferChunkRequest =
        """{"transfer":"inline","hash":"abc123","team_id":7,"transfer_id":42,"chunk":4096,"file_id":123456789,"start":0,"end":262144}""";

    [Fact]
    public void InlineTransferRequestIsRecognisedRatherThanMisroutedToTelemetry()
    {
        // Arrange
        using JsonDocument document = JsonDocument.Parse(InlineTransferChunkRequest);

        // Act
        // TelemetryDTO.Status is required, so mis-routing this into the telemetry branch would throw
        // a JsonException here - which WebSocketHandler would in turn treat as a protocol violation
        // and close the printer's socket mid-upload.
        ConnectionMessage? message = NewDispatcher().Classify(printerId: 1, document.RootElement);

        // Assert
        message.Should().BeOfType<InboundTransferRequestMessage>();
    }

    /// <summary>
    /// A root that is not an object is refused with <see cref="JsonException"/> - the one exception
    /// both transports treat as the printer's protocol violation. <see cref="JsonElement"/>'s
    /// property accessors would otherwise throw <see cref="InvalidOperationException"/>, which the
    /// HTTP transport answered with a 500 and the socket closed on as if nothing were wrong.
    /// </summary>
    /// <param name="json">Valid JSON of every kind but an object.</param>
    [Theory]
    [InlineData("[1]")]
    [InlineData("\"PRINTING\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void ARootThatIsNotAnObjectIsAProtocolViolation(string json)
    {
        // Arrange
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        // Act
        Action classify = () => NewDispatcher().Classify(printerId: 1, root);

        // Assert
        classify.Should().Throw<JsonException>();
    }

    /// <summary>
    /// A <c>transfer</c> that is not a string is not an inline request, and must not throw on the way
    /// to finding that out - <see cref="JsonElement.ValueEquals(string)"/> throws on any other kind.
    /// </summary>
    [Fact]
    public void ATransferThatIsNotAStringFallsThroughToTelemetry()
    {
        // Arrange
        using JsonDocument document = JsonDocument.Parse("""{"transfer":5,"state":"IDLE"}""");

        // Act
        ConnectionMessage? message = NewDispatcher().Classify(printerId: 1, document.RootElement);

        // Assert
        message.Should().BeOfType<InboundTelemetryMessage>();
    }

    /// <summary>Minimal valid telemetry - only the one required field, <c>state</c>. Every other
    /// field is nullable on the telemetry DTO, so this is the true happy-path floor.</summary>
    private const string MinimalTelemetry = """{"state":"PRINTING"}""";

    [Fact]
    public void TelemetryMessageClassifiesAsTelemetry()
    {
        // Arrange
        using JsonDocument document = JsonDocument.Parse(MinimalTelemetry);

        // Act
        // No "event" and no "transfer":"inline" marker, so this must fall through to the telemetry
        // branch - the case the other two branches exist to route around.
        ConnectionMessage? message = NewDispatcher().Classify(printerId: 1, document.RootElement);

        // Assert
        InboundTelemetryMessage telemetry = message.Should().BeOfType<InboundTelemetryMessage>().Subject;
        telemetry.Telemetry.Status.Should().Be("PRINTING");
    }

    /// <summary>Minimal valid event - <c>event</c> and <c>state</c> are the only required fields.</summary>
    /// <summary>
    /// The status is a string on the wire, so the trace line carries it cleaned and cut to the length
    /// of a status - while the message itself keeps what was sent, for whatever reads it next.
    /// </summary>
    [Fact]
    public void TheTelemetryTraceCarriesTheStatusCleaned()
    {
        // Arrange - the escape spelt as JSON spells it
        using JsonDocument document = JsonDocument.Parse("{\"state\":\"IDLE\\u001B[2J" + new string('x', 100) + "\"}");
        FakeLogger<MessageDispatcher> logger = new();
        MessageDispatcher dispatcher = new(logger, NewTracker(), TimeProvider.System, PrinterTrafficLogTests.Off);

        // Act
        dispatcher.Classify(printerId: 1, document.RootElement);

        // Assert
        logger.Collector.GetSnapshot().Should()
              .ContainSingle(record => record.Level == LogLevel.Trace)
              .Which.StructuredState.Should().Contain(
                  pair => pair.Key == "State" &&
                          pair.Value!.StartsWith("IDLE\uFFFD[2Jx", StringComparison.Ordinal) &&
                          pair.Value.EndsWith("<108 characters in all>", StringComparison.Ordinal));
    }

    private const string MinimalEvent = """{"event":"INFO","state":"IDLE"}""";

    [Fact]
    public void EventMessageClassifiesAsEvent()
    {
        // Arrange
        using JsonDocument document = JsonDocument.Parse(MinimalEvent);

        // Act
        ConnectionMessage? message = NewDispatcher().Classify(printerId: 1, document.RootElement);

        // Assert
        InboundEventMessage inboundEvent = message.Should().BeOfType<InboundEventMessage>().Subject;
        inboundEvent.Event.EventType.Should().Be(Homespool.Model.PrinterEventType.Info);
    }

    /// <summary>The <c>command_id</c> a command ack correlates on must survive classification - it
    /// is the one field the actor's correlation depends on.</summary>
    private const string FinishedEventWithCommandId = """{"event":"FINISHED","state":"IDLE","command_id":42}""";

    [Fact]
    public void CommandIdSurvivesClassification()
    {
        // Arrange
        using JsonDocument document = JsonDocument.Parse(FinishedEventWithCommandId);

        // Act
        ConnectionMessage? message = NewDispatcher().Classify(printerId: 1, document.RootElement);

        // Assert
        InboundEventMessage inboundEvent = message.Should().BeOfType<InboundEventMessage>().Subject;
        inboundEvent.Event.CommandId.Should().Be(42u);
        inboundEvent.Event.EventType.Should().Be(Homespool.Model.PrinterEventType.Finished);
    }

    /// <summary>
    /// A mended message is classified like any other, and says so once: what is stored will show no
    /// reading where the printer sent one of these, and this line is what explains why.
    /// </summary>
    [Fact]
    public void AMendedMessageIsClassifiedAndSaysWhatWasReplaced()
    {
        // Arrange - as mended from a FILE_INFO carrying layer_height nan and max_layer_z inf, twice nan
        using JsonDocument document = JsonDocument.Parse(
            """{"event":"FILE_INFO","state":"IDLE","data":{"layer_height":"NaN","max_layer_z":"Infinity","x":"NaN"}}""");
        FakeLogger<MessageDispatcher> logger = new();
        MessageDispatcher dispatcher = new(logger, NewTracker(), TimeProvider.System, PrinterTrafficLogTests.Off);

        // Act
        ConnectionMessage? message = dispatcher.Classify(
            printerId: 7,
            document.RootElement,
            [new NonFiniteToken(2, "nan"), new NonFiniteToken(3, "inf"), new NonFiniteToken(4, "nan")]);

        // Assert
        message.Should().BeOfType<InboundEventMessage>();

        FakeLogRecord warning = logger.Collector.GetSnapshot().Should()
                                      .ContainSingle(record => record.Level == LogLevel.Warning).Subject;

        warning.StructuredState.Should().Contain(pair => pair.Key == "PrinterId" && pair.Value == "7");
        warning.StructuredState.Should().Contain(pair => pair.Key == "Count" && pair.Value == "3");
        warning.StructuredState.Should().Contain(pair => pair.Key == "Spellings" && pair.Value == "nan inf");
    }

    /// <summary>
    /// An <c>INFO</c> whose nozzle diameter was mended to a quoted literal still yields its identity,
    /// carrying the float the literal names. Read without accepting that form, the payload "could
    /// not be read" and the whole identity - firmware, model, serial - would be dropped with it.
    /// </summary>
    [Fact]
    public void AnInfoWithAQuotedLiteralStillYieldsItsIdentity()
    {
        // Arrange - as mended from "nozzle_diameter":inf
        using JsonDocument document = JsonDocument.Parse(
            """{"event":"INFO","state":"IDLE","data":{"firmware":"6.10.1","nozzle_diameter":"Infinity"}}""");

        // Act
        ConnectionMessage? message = NewDispatcher().Classify(printerId: 1, document.RootElement, [new NonFiniteToken(3, "inf")]);

        // Assert
        InboundEventMessage inboundEvent = message.Should().BeOfType<InboundEventMessage>().Subject;

        inboundEvent.Identity.Should().NotBeNull();
        inboundEvent.Identity.Firmware.Should().Be("6.10.1");
        inboundEvent.Identity.NozzleDiameter.Should().Be(float.PositiveInfinity, "judging it is the writer's job, where it would be stored");
    }

    [Fact]
    public void AnOrdinaryMessageSaysNothingAboutNonFiniteNumbers()
    {
        // Arrange
        using JsonDocument document = JsonDocument.Parse(MinimalEvent);
        FakeLogger<MessageDispatcher> logger = new();
        MessageDispatcher dispatcher = new(logger, NewTracker(), TimeProvider.System, PrinterTrafficLogTests.Off);

        // Act
        dispatcher.Classify(printerId: 7, document.RootElement);

        // Assert
        logger.Collector.GetSnapshot().Should().NotContain(record => record.Level == LogLevel.Warning);
    }

    [Fact]
    public void ReceivedAtIsStampedAtClassificationTime()
    {
        // Arrange
        using JsonDocument document = JsonDocument.Parse(MinimalTelemetry);
        DateTimeOffset before = DateTimeOffset.UtcNow;

        // Act
        ConnectionMessage? message = NewDispatcher().Classify(printerId: 1, document.RootElement);

        // Assert
        // The timestamp is taken here, on the read loop, not when the actor gets around to the
        // message - so a backlog in the mailbox can never skew when a sample claims to have arrived.
        DateTimeOffset after = DateTimeOffset.UtcNow;
        InboundTelemetryMessage telemetry = message.Should().BeOfType<InboundTelemetryMessage>().Subject;
        telemetry.ReceivedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }
}
