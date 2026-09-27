using System.Collections.Generic;

namespace Homespool.Host.Telemetry;

/// <summary>
/// The plate's cancellable objects as a printer just reported them, lifted out of the event so it
/// can be stored as the printer's current state rather than only inside the event's payload.
/// </summary>
/// <remarks>
/// <para>
/// <b>A separate carrier for the reason <see cref="PrinterAttentionUpdate"/> is one</b>: the event row
/// keeps what arrived, and anything that must be readable as current is lifted to a place holding one
/// value per printer. It matters more here than there, because the payload a stored event keeps is
/// capped, and a full plate's report is far larger than the cap.
/// </para>
/// <para>
/// <b>Always the whole set.</b> Firmware renders every object on each report, so an update replaces
/// what is stored and is never merged into it. An empty one is the printer saying there is nothing to
/// cancel, which is what it sends outside a print.
/// </para>
/// </remarks>
/// <param name="ObjectCount">How many objects the print declares. Ids run from zero to one below.</param>
/// <param name="CancelledIds">The ids of the objects that are cancelled.</param>
public sealed record PrinterCancellableUpdate(int ObjectCount, IReadOnlyCollection<int> CancelledIds);
