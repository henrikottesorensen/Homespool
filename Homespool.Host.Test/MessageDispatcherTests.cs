using System;
using System.Text.Json;

using AwesomeAssertions;
using Homespool.Host.PrusaConnect;
using Microsoft.Extensions.Logging.Abstractions;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="MessageDispatcher.Classify"/> - one parsed wire message in, one typed
/// <see cref="ConnectionMessage"/> out. Pure classification: what the actor <i>does</i> with each
/// message (correlation, the sink) is covered by <see cref="PrinterConnectionActorTests"/>.
/// </summary>
public class MessageDispatcherTests
{
    private static MessageDispatcher NewDispatcher() => new(NullLogger<MessageDispatcher>.Instance);

    /// <summary>
    /// The <c>InlineRequest</c> shape from firmware's <c>render.cpp:100-119</c>
    /// (<c>transfers::Download::InlineRequest</c>) - the printer asking for the next chunk of a
    /// Connect-initiated file upload. No captured message of this shape exists (see
    /// <c>notes/transfer-protocol.md</c>), so this is built from the documented firmware source
    /// rather than replayed. Has neither <c>"event"</c> nor <c>"state"</c>, which is exactly what
    /// would previously have mis-routed it into the telemetry branch.
    /// </summary>
    private const string InlineTransferChunkRequest =
        """{"transfer":"inline","hash":"abc123","team_id":7,"transfer_id":42,"chunk":4096,"file_id":123456789,"start":0,"end":262144}""";

    [Fact]
    public void InlineTransferRequestIsRecognizedRatherThanMisroutedToTelemetry()
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
        inboundEvent.Event.EventType.Should().Be(Homespool.Model.Events.Info);
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
        inboundEvent.Event.EventType.Should().Be(Homespool.Model.Events.Finished);
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
