using System;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using PrinterService.Host.PrusaConnect.DTO.EventMessages;
using PrinterService.Host.PrusaConnect.DTO.Telemetry;

namespace PrinterService.Host.PrusaConnect;

/// <summary>
/// Classifies one parsed WebSocket message into a typed <see cref="ConnectionMessage"/> for the
/// printer's <see cref="PrinterConnectionActor"/>. Pure: deserializes and logs, no side effects on
/// connection state - correlation and persistence happen on the actor's loop. Deliberately
/// transport-agnostic - takes the parsed <see cref="JsonElement"/> plus an already-resolved printer
/// id, touches neither <c>HttpContext</c> nor the socket - so the deferred HTTP transport
/// (<c>POST /p/telemetry</c>, <c>POST /p/events</c>) can reuse it unchanged.
/// </summary>
/// <remarks>
/// Deserialization stays here, on the read loop, rather than moving to the actor: a message that
/// fails to deserialize is a protocol violation, and <see cref="WebSocketHandler"/>'s
/// <see cref="JsonException"/> handling - close the socket on garbage - only works if the throw
/// happens on its call stack. The log lines stay too - they were the only visibility into the
/// stream before persistence existed, and remain useful for anything not yet queryable from the
/// database.
/// </remarks>
public class MessageDispatcher
{
    private readonly ILogger<MessageDispatcher> _logger;

    public MessageDispatcher(ILogger<MessageDispatcher> logger)
    {
        _logger = logger;
    }

    /// <returns>The typed message to post to the printer's actor. Null means "post nothing" - no
    /// production shape maps to it, but test spies use it to observe the stream without an actor.</returns>
    public virtual ConnectionMessage? Classify(int printerId, JsonElement root)
    {
        DateTimeOffset receivedAt = TimeProvider.System.GetUtcNow();

        if (root.TryGetProperty("event", out _))
        {
            EventDTO eventDto = root.Deserialize<EventDTO>()!;

            _logger.LogDebug("[{PrinterId}] event {EventType}", printerId, eventDto.EventType);

            return new InboundEventMessage(receivedAt, eventDto);
        }

        if (root.TryGetProperty("transfer", out JsonElement transfer) && transfer.ValueEquals("inline"))
        {
            // transfers::Download::InlineRequest (render.cpp:100-119) - the printer requesting the
            // next chunk of a Connect-initiated file upload. Has neither "event" nor "state", so it
            // would otherwise fall into the telemetry branch and fail TelemetryDTO's required Status.
            return new InboundTransferRequestMessage();
        }

        TelemetryDTO telemetryDto = root.Deserialize<TelemetryDTO>()!;

        // Trace, one level below the others: telemetry arrives roughly once a second per printer,
        // vs. events/transfer requests, which are merely frequent-per-printer rather than continuous.
        _logger.LogTrace("[{PrinterId}] telemetry state={State}", printerId, telemetryDto.Status);

        return new InboundTelemetryMessage(receivedAt, telemetryDto);
    }
}
