using System;
using System.Threading;
using System.Threading.Tasks;

namespace Homespool.Host.Telemetry;

/// <summary>
/// The writer's refusal of one printer's telemetry while that printer is being removed. Returned by
/// <see cref="ITelemetryEviction.BeginEvictionAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Disposing it always releases the hold</b>, and that is the whole of the failure path: a delete
/// that throws, or is never reached, leaves the printer row and its credential in place, the printer
/// reconnects, and its telemetry has to be accepted again. Hold it in an <c>await using</c> so no
/// exit can skip the release.
/// </para>
/// <para>
/// <b>Holds are counted.</b> Two removals of one printer can both get as far as the delete; if one
/// commits and the other then fails on the missing row, the failed one's release must not un-refuse a
/// printer that really is gone. <see cref="CompleteAsync"/> is what says so, and once it has been
/// called no release undoes it.
/// </para>
/// </remarks>
public interface IPrinterEviction : IAsyncDisposable
{
    /// <summary>
    /// Says the printer row has been deleted: refuses the printer's telemetry for the life of the
    /// process, and deletes what the telemetry store holds for it.
    /// </summary>
    /// <remarks>
    /// Called only after the delete has committed. The stored rows are history a failed removal
    /// would otherwise have destroyed for a printer that is still there.
    /// </remarks>
    Task CompleteAsync(CancellationToken cancellationToken);
}
