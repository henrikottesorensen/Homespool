using System.Threading;
using System.Threading.Tasks;

namespace Homespool.Host.Telemetry;

/// <summary>
/// Tells the writer that a printer is being removed, so it drops what it still holds about one
/// instead of trying to persist it. <see cref="TelemetryWriter"/> is the only implementation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from <see cref="ITelemetrySink"/> deliberately.</b> That interface is where a protocol
/// edge hands off what it heard, and every implementation of it is an edge test's double; this is the
/// opposite direction and has one caller, so folding the two together would make every such double
/// implement a method it never calls.
/// </para>
/// <para>
/// <b>Why removing a printer needs this at all.</b> A flush commits its whole batch in one
/// transaction and, on failure, keeps the buffers for the next attempt - so one row referencing a
/// printer that no longer exists fails the batch for <em>every</em> printer, and re-fails on each
/// retry until the buffer ceilings trim it away. The suite already relies on this: removing the
/// printer row is how <c>TelemetryWriterTests</c> injects a flush failure.
/// </para>
/// <para>
/// <b>An eviction is provisional until the row is gone.</b> The buffers have to be purged before the
/// delete is attempted, but the delete can still fail, and a printer whose row survives keeps its
/// credential and reconnects. Refusing its telemetry for good at that point would leave a connected
/// printer that never reports anything again until a restart. So the refusal is held by an
/// <see cref="IPrinterEviction"/>, made permanent by <see cref="IPrinterEviction.CompleteAsync"/>
/// once the delete has committed, and released by disposing it otherwise.
/// </para>
/// </remarks>
public interface ITelemetryEviction
{
    /// <summary>
    /// Drops everything buffered for <paramref name="printerId"/> and refuses anything further that
    /// arrives for it while the returned eviction is held. Completes once the writer has actually
    /// done so, which is what makes it safe to delete the row afterwards.
    /// </summary>
    /// <remarks>
    /// Cancelling the wait releases the hold rather than leaving it behind - there is no eviction for
    /// the caller to dispose.
    /// </remarks>
    Task<IPrinterEviction> BeginEvictionAsync(int printerId, CancellationToken cancellationToken);
}
