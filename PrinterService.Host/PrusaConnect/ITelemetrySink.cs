using System;

using PrinterService.Host.PrusaConnect.DTO.EventMessages;
using PrinterService.Host.PrusaConnect.DTO.Telemetry;

namespace PrinterService.Host.PrusaConnect;

/// <summary>
/// Where <see cref="MessageDispatcher"/> hands off a parsed message for persistence, instead of
/// touching a <see cref="PrinterService.Data.PSDbContext"/> itself. <see cref="TelemetryWriter"/> is
/// the only implementation; the interface exists so <c>MessageDispatcherTests</c> can assert what
/// gets enqueued without a real channel or database.
/// </summary>
public interface ITelemetrySink
{
    void Enqueue(int printerId, DateTimeOffset receivedAt, TelemetryDTO telemetry);

    void Enqueue(int printerId, DateTimeOffset receivedAt, EventDTO eventDto);
}
