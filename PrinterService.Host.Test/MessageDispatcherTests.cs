using System;
using System.Collections.Generic;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using PrinterService.Host.PrusaConnect;
using PrinterService.Host.PrusaConnect.DTO.EventMessages;
using PrinterService.Host.PrusaConnect.DTO.Telemetry;

namespace PrinterService.Host.Test;

public class MessageDispatcherTests
{
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
        RecordingTelemetrySink sink = new();
        MessageDispatcher dispatcher = new(NullLogger<MessageDispatcher>.Instance, sink, new PrinterCommandCorrelator());

        // Act
        // TelemetryDTO.Status is required, so mis-routing this into the telemetry branch would throw
        // a JsonException here - which WebSocketHandler would in turn treat as a protocol violation
        // and close the printer's socket mid-upload.
        Action act = () => dispatcher.Dispatch(printerId: 1, document.RootElement);

        // Assert
        act.Should().NotThrow();

        // Nothing to persist yet for this message shape - the transfer feature isn't built - so it
        // must not silently produce a telemetry or event row either.
        sink.TelemetryCalls.Should().BeEmpty();
        sink.EventCalls.Should().BeEmpty();
    }

    /// <summary>Minimal valid telemetry - only the one required field, <c>state</c>. Every other
    /// field is nullable on <see cref="TelemetryDTO"/>, so this is the true happy-path floor.</summary>
    private const string MinimalTelemetry = """{"state":"PRINTING"}""";

    [Fact]
    public void TelemetryMessageDispatchesWithoutThrowing()
    {
        // Arrange
        using JsonDocument document = JsonDocument.Parse(MinimalTelemetry);
        RecordingTelemetrySink sink = new();
        MessageDispatcher dispatcher = new(NullLogger<MessageDispatcher>.Instance, sink, new PrinterCommandCorrelator());

        // Act
        // No "event" and no "transfer":"inline" marker, so this must fall through to the telemetry
        // branch - the case the other two branches exist to route around.
        Action act = () => dispatcher.Dispatch(printerId: 1, document.RootElement);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void TelemetryMessageIsHandedToTheSink()
    {
        // Arrange
        using JsonDocument document = JsonDocument.Parse(MinimalTelemetry);
        RecordingTelemetrySink sink = new();
        MessageDispatcher dispatcher = new(NullLogger<MessageDispatcher>.Instance, sink, new PrinterCommandCorrelator());

        // Act
        dispatcher.Dispatch(printerId: 7, document.RootElement);

        // Assert
        sink.TelemetryCalls.Should().ContainSingle();
        sink.TelemetryCalls[0].PrinterId.Should().Be(7);
        sink.TelemetryCalls[0].Telemetry.Status.Should().Be("PRINTING");
        sink.EventCalls.Should().BeEmpty();
    }

    /// <summary>Minimal valid event - <c>event</c> and <c>state</c> are the only required fields on
    /// <see cref="EventDTO"/>.</summary>
    private const string MinimalEvent = """{"event":"INFO","state":"IDLE"}""";

    [Fact]
    public void EventMessageDispatchesWithoutThrowing()
    {
        // Arrange
        using JsonDocument document = JsonDocument.Parse(MinimalEvent);
        RecordingTelemetrySink sink = new();
        MessageDispatcher dispatcher = new(NullLogger<MessageDispatcher>.Instance, sink, new PrinterCommandCorrelator());

        // Act
        Action act = () => dispatcher.Dispatch(printerId: 1, document.RootElement);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void EventMessageIsHandedToTheSink()
    {
        // Arrange
        using JsonDocument document = JsonDocument.Parse(MinimalEvent);
        RecordingTelemetrySink sink = new();
        MessageDispatcher dispatcher = new(NullLogger<MessageDispatcher>.Instance, sink, new PrinterCommandCorrelator());

        // Act
        dispatcher.Dispatch(printerId: 3, document.RootElement);

        // Assert
        sink.EventCalls.Should().ContainSingle();
        sink.EventCalls[0].PrinterId.Should().Be(3);
        sink.EventCalls[0].Event.EventType.Should().Be(PrinterService.Model.Events.Info);
        sink.TelemetryCalls.Should().BeEmpty();
    }

    /// <summary>An event whose <c>command_id</c> matches a pending command completes it with that
    /// event's outcome - the real ack path (planner.cpp:667-790 at the pinned firmware ref), not a
    /// heuristic.</summary>
    private const string FinishedEventWithCommandId = """{"event":"FINISHED","state":"IDLE","command_id":42}""";

    [Fact]
    public async System.Threading.Tasks.Task MatchingCommandIdOnEventCompletesThePendingCommand()
    {
        // Arrange
        using JsonDocument document = JsonDocument.Parse(FinishedEventWithCommandId);
        RecordingTelemetrySink sink = new();
        PrinterCommandCorrelator correlator = new();
        correlator.TryBeginCommand(printerId: 1, commandId: 42, out System.Threading.Tasks.Task<CommandOutcome> outcome);
        MessageDispatcher dispatcher = new(NullLogger<MessageDispatcher>.Instance, sink, correlator);

        // Act
        dispatcher.Dispatch(printerId: 1, document.RootElement);

        // Assert
        CommandOutcome result = await outcome;
        result.EventType.Should().Be(PrinterService.Model.Events.Finished);
    }

    [Fact]
    public void NonMatchingCommandIdOnEventLeavesThePendingCommandOutstanding()
    {
        // Arrange
        using JsonDocument document = JsonDocument.Parse(FinishedEventWithCommandId);
        RecordingTelemetrySink sink = new();
        PrinterCommandCorrelator correlator = new();
        correlator.TryBeginCommand(printerId: 1, commandId: 99, out System.Threading.Tasks.Task<CommandOutcome> outcome);
        MessageDispatcher dispatcher = new(NullLogger<MessageDispatcher>.Instance, sink, correlator);

        // Act
        dispatcher.Dispatch(printerId: 1, document.RootElement);

        // Assert
        // command_id 42 on the wire doesn't match the pending 99, so the pending command must not be
        // mistaken as answered by an unrelated event.
        outcome.IsCompleted.Should().BeFalse();
    }

    /// <summary>Records every call instead of acting on it, matching this project's hand-rolled,
    /// no-mocking-framework style for fakes (e.g. <c>StaticOptionsMonitor</c>).</summary>
    private sealed class RecordingTelemetrySink : ITelemetrySink
    {
        public List<(int PrinterId, DateTimeOffset ReceivedAt, TelemetryDTO Telemetry)> TelemetryCalls { get; } = [];

        public List<(int PrinterId, DateTimeOffset ReceivedAt, EventDTO Event)> EventCalls { get; } = [];

        public void Enqueue(int printerId, DateTimeOffset receivedAt, TelemetryDTO telemetry)
        {
            TelemetryCalls.Add((printerId, receivedAt, telemetry));
        }

        public void Enqueue(int printerId, DateTimeOffset receivedAt, EventDTO eventDto)
        {
            EventCalls.Add((printerId, receivedAt, eventDto));
        }
    }
}
