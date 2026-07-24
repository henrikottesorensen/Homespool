using System;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using PrinterService.Host.PrusaConnect.DTO.EventMessages;
using PrinterService.Host.PrusaConnect.DTO.Telemetry;

namespace PrinterService.Host.PrusaConnect;

/// <summary>
/// Routes one parsed WebSocket message to its DTO. Deliberately transport-agnostic - takes the
/// parsed <see cref="JsonElement"/> plus an already-resolved printer id, touches neither
/// <c>HttpContext</c> nor the socket - so the deferred HTTP transport (<c>POST /p/telemetry</c>,
/// <c>POST /p/events</c>) can reuse it unchanged.
/// </summary>
/// <remarks>
/// Deserializes and logs, same as before, then hands the DTO to <see cref="ITelemetrySink"/> for
/// <see cref="TelemetryWriter"/> to persist. The log lines stay - they were the only visibility into
/// the stream before persistence existed, and remain useful for anything not yet queryable from the
/// database.
/// </remarks>
public class MessageDispatcher
{
    private readonly ILogger<MessageDispatcher> _logger;
    private readonly ITelemetrySink _sink;

    public MessageDispatcher(ILogger<MessageDispatcher> logger, ITelemetrySink sink)
    {
        _logger = logger;
        _sink = sink;
    }

    public virtual void Dispatch(int printerId, JsonElement root)
    {
        DateTimeOffset receivedAt = TimeProvider.System.GetUtcNow();

        if (root.TryGetProperty("event", out _))
        {
            EventDTO eventDto = root.Deserialize<EventDTO>()!;

            _logger.LogDebug("[{PrinterId}] event {EventType}", printerId, eventDto.EventType);
            _sink.Enqueue(printerId, receivedAt, eventDto);
        }
        else if (root.TryGetProperty("transfer", out JsonElement transfer) && transfer.ValueEquals("inline"))
        {
            // transfers::Download::InlineRequest (render.cpp:100-119) - the printer requesting the
            // next chunk of a Connect-initiated file upload. Has neither "event" nor "state", so it
            // would otherwise fall into the telemetry branch and fail TelemetryDTO's required Status.
            // Serving chunks back is a separate, much larger feature (notes/transfer-protocol.md);
            // recognized-but-out-of-scope for now, not an error, and nothing to persist yet either.
            _logger.LogDebug("[{PrinterId}] inline transfer chunk request (not yet served)", printerId);
        }
        else
        {
            TelemetryDTO telemetryDto = root.Deserialize<TelemetryDTO>()!;

            // Trace, one level below the others: telemetry arrives roughly once a second per
            // printer, vs. events/transfer requests, which are merely frequent-per-printer rather
            // than continuous.
            _logger.LogTrace("[{PrinterId}] telemetry state={State}", printerId, telemetryDto.Status);
            _sink.Enqueue(printerId, receivedAt, telemetryDto);
        }
    }
}
