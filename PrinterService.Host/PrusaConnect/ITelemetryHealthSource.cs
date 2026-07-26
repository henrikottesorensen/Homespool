using System;

namespace PrinterService.Host.PrusaConnect;

/// <summary>
/// Exposes <see cref="TelemetryWriter"/>'s persistence health without exposing the writer itself, so
/// the health check can be tested against a stub rather than a running background service.
/// </summary>
public interface ITelemetryHealthSource
{
    TelemetryHealthSnapshot Current { get; }

    /// <summary>
    /// Whether the drain loop is still going.
    /// </summary>
    /// <remarks>
    /// The one persistence fault a restart actually fixes, and the only one fit for a liveness
    /// probe. If the loop stops, the service keeps serving pages and holding its WebSocket
    /// connections while nothing is ever written again - invisible from outside, and unlike a
    /// rejecting database, genuinely cured by starting over.
    ///
    /// A writer that has not started yet reports true, not false. Hosted services start after the
    /// server begins listening, so a probe arriving in that window must not be able to kill the
    /// process; only a loop that has actually completed counts as stopped.
    /// </remarks>
    bool IsDraining { get; }
}
