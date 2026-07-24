using System;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using PrinterService.Host.PrusaConnect;

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
        MessageDispatcher dispatcher = new(NullLogger<MessageDispatcher>.Instance);

        // Act
        // TelemetryDTO.Status is required, so mis-routing this into the telemetry branch would throw
        // a JsonException here - which WebSocketHandler would in turn treat as a protocol violation
        // and close the printer's socket mid-upload.
        Action act = () => dispatcher.Dispatch(printerId: 1, document.RootElement);

        // Assert
        act.Should().NotThrow();
    }

    /// <summary>Minimal valid telemetry - only the one required field, <c>state</c>. Every other
    /// field is nullable on <see cref="TelemetryDTO"/>, so this is the true happy-path floor.</summary>
    private const string MinimalTelemetry = """{"state":"PRINTING"}""";

    [Fact]
    public void TelemetryMessageDispatchesWithoutThrowing()
    {
        // Arrange
        using JsonDocument document = JsonDocument.Parse(MinimalTelemetry);
        MessageDispatcher dispatcher = new(NullLogger<MessageDispatcher>.Instance);

        // Act
        // No "event" and no "transfer":"inline" marker, so this must fall through to the telemetry
        // branch - the case the other two branches exist to route around.
        Action act = () => dispatcher.Dispatch(printerId: 1, document.RootElement);

        // Assert
        act.Should().NotThrow();
    }

    /// <summary>Minimal valid event - <c>event</c> and <c>state</c> are the only required fields on
    /// <see cref="EventDTO"/>.</summary>
    private const string MinimalEvent = """{"event":"INFO","state":"IDLE"}""";

    [Fact]
    public void EventMessageDispatchesWithoutThrowing()
    {
        // Arrange
        using JsonDocument document = JsonDocument.Parse(MinimalEvent);
        MessageDispatcher dispatcher = new(NullLogger<MessageDispatcher>.Instance);

        // Act
        Action act = () => dispatcher.Dispatch(printerId: 1, document.RootElement);

        // Assert
        act.Should().NotThrow();
    }
}
