using System;

using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Host.PrusaConnect.DTO.Telemetry;

namespace Homespool.Host.Telemetry;

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
