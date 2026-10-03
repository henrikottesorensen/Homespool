using Homespool.Model;

namespace Homespool.Host.Telemetry;

/// <summary>
/// Told by <see cref="TelemetryWriter"/> which printers' events have just been saved, so whatever
/// reads the event log can read it now rather than on a timer.
/// </summary>
/// <remarks>
/// <para>
/// <b>After the save, never before</b>: an observer that goes to the log on being told must find the
/// rows there. A flush that fails tells nobody, and its events are not retried, so there is nothing
/// to read.
/// </para>
/// <para>
/// <b>Called on the writer's loop, so an implementation must return at once and must not throw</b>,
/// as <see cref="ILiveStateObserver"/>'s must: post a message and leave. The writer catches what an
/// implementation throws anyway.
/// </para>
/// </remarks>
public interface IPrinterEventObserver
{
    /// <summary>An event of <paramref name="eventType"/> from <paramref name="printerId"/> is in the log.</summary>
    /// <param name="printerId">The printer.</param>
    /// <param name="eventType">What the event was.</param>
    void Saved(int printerId, PrinterEventType eventType);
}
