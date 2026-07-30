using System;
using System.Text.Json;

using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Host.PrusaConnect.DTO.Telemetry;
using Homespool.Host.PrusaConnect.DTO.Transfers;
using Microsoft.Extensions.Logging;

namespace Homespool.Host.PrusaConnect;

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
    private readonly UnknownFieldTracker _unknownFields;
    private readonly TimeProvider _timeProvider;

    public MessageDispatcher(ILogger<MessageDispatcher> logger,
                             UnknownFieldTracker unknownFields,
                             TimeProvider timeProvider)
    {
        _logger = logger;
        _unknownFields = unknownFields;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Sorts one parsed message off the wire into its typed form: an <c>event</c> property means the
    /// event path, anything else is telemetry. This is the only place that decision is made.
    /// </summary>
    /// <returns>The typed message to post to the printer's actor. Null means "post nothing" - no
    /// production shape maps to it, but test spies use it to observe the stream without an actor.</returns>
    public virtual ConnectionMessage? Classify(int printerId, JsonElement root)
    {
        DateTimeOffset receivedAt = _timeProvider.GetUtcNow();

        if (root.TryGetProperty("event", out _))
        {
            EventDTO eventDto = root.Deserialize<EventDTO>()!;

            _logger.LogDebug("event {EventType}", eventDto.EventType);

            // Qualified by event type: an unmodelled envelope key on a FILE_INFO and the same key on
            // a STATE_CHANGED are two findings, not one. Only the envelope - eventDto.Data is raw and
            // is never walked, for the reason UnknownFieldTracker's remarks give at length.
            _unknownFields.Record(printerId, $"event:{eventDto.EventType}", eventDto.Unknown);

            return new InboundEventMessage(receivedAt, eventDto);
        }

        if (root.TryGetProperty("transfer", out JsonElement transfer) && transfer.ValueEquals("inline"))
        {
            // transfers::Download::InlineRequest (render.cpp:100-119) - the printer requesting the
            // next chunk of a Connect-initiated file upload. Has neither "event" nor "state", so it
            // would otherwise fall into the telemetry branch and fail TelemetryDTO's required Status.
            InlineRequestDTO request = root.Deserialize<InlineRequestDTO>()!;

            _logger.LogDebug("transfer chunk request file_id={FileId} {Start}..{End}",
                request.FileId, request.Start, request.End);

            return new InboundTransferRequestMessage(receivedAt, request);
        }

        TelemetryDTO telemetryDto = root.Deserialize<TelemetryDTO>()!;

        // Trace, one level below the others: telemetry arrives roughly once a second per printer,
        // vs. events/transfer requests, which are merely frequent-per-printer rather than continuous.
        _logger.LogTrace("telemetry state={State}", telemetryDto.Status);

        // Each nested shape named explicitly rather than walked by reflection. The explicitness is
        // the safeguard: there is no traversal that could wander into somewhere unbounded, and
        // adding a shape is a deliberate edit. "slot" is absent on purpose - SlotsTelemetryDTO
        // already spends its one permitted extension-data property on the numbered slots themselves.
        _unknownFields.Record(printerId, "telemetry", telemetryDto.Unknown);
        _unknownFields.Record(printerId, "telemetry.chamber", telemetryDto.Chamber?.Unknown);
        _unknownFields.Record(printerId, "telemetry.enclosure", telemetryDto.Enclosure?.Unknown);

        return new InboundTelemetryMessage(receivedAt, telemetryDto);
    }
}
