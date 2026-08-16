using System;

namespace Homespool.Host.Telemetry;

/// <summary>
/// Where a protocol edge hands off what it heard for persistence, instead of touching a
/// <see cref="Homespool.Data.HomespoolDbContext"/> itself. <see cref="TelemetryWriter"/> is the
/// only implementation; the interface exists so edge tests can assert what gets enqueued without
/// a real channel or database.
/// </summary>
/// <remarks>
/// The currency is neutral by design: an edge converts its wire's shapes into
/// <see cref="TelemetryUpdate"/>/<see cref="PrinterEventRecord"/> - stating its merge policy per
/// field on the way, see <see cref="Field{T}"/> - and nothing downstream of this interface knows
/// which protocol spoke. For Prusa Connect the conversion is <c>PrusaTelemetryMapping</c>.
/// </remarks>
public interface ITelemetrySink
{
    void Enqueue(int printerId, DateTimeOffset receivedAt, TelemetryUpdate telemetry);

    void Enqueue(int printerId, DateTimeOffset receivedAt, PrinterEventRecord eventRecord);
}
