using System;

using PrinterService.Host.PrusaConnect.DTO.EventMessages;
using PrinterService.Host.PrusaConnect.DTO.Telemetry;

namespace PrinterService.Host.PrusaConnect;

/// <summary>
/// One message queued for <see cref="TelemetryWriter"/> to persist. A closed hierarchy rather than
/// two separate channels, so the writer drains a single ordered stream per printer instead of
/// reasoning about interleaving between them.
/// </summary>
public abstract record TelemetryWriteItem(int PrinterId, DateTimeOffset ReceivedAt)
{
    public sealed record TelemetryItem(int PrinterId, DateTimeOffset ReceivedAt, TelemetryDTO Data)
        : TelemetryWriteItem(PrinterId, ReceivedAt);

    public sealed record EventItem(int PrinterId, DateTimeOffset ReceivedAt, EventDTO Data)
        : TelemetryWriteItem(PrinterId, ReceivedAt);
}
