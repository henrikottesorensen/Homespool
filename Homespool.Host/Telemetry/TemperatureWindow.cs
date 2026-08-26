using System;

using Homespool.Model.Entities;

namespace Homespool.Host.Telemetry;

/// <summary>
/// Which stretch of time the printer page's temperature graph covers.
/// </summary>
/// <remarks>
/// <para>
/// <b>A running job sets the window, because that is the thing being watched.</b> A fixed hour
/// either cuts a long print off at its most boring end or pads a short one with idle - where a
/// job-scoped graph starts at the heat-up and ends at now, which is the shape somebody came to
/// look at.
/// </para>
/// <para>
/// <b>The job's age comes from <see cref="PrinterLiveState.TimePrinting"/>, not from a
/// <c>PrintJob</c> row.</b> A print started at the printer's own panel has no row here - only prints
/// this application queued do - and it is exactly as worth graphing. Firmware guards the whole job
/// block with <c>if (params.has_job)</c>, so a non-null value is the printer's own statement that a
/// job is running rather than an inference from <see cref="PrinterLiveState.Status"/>.
/// </para>
/// </remarks>
public static class TemperatureWindow
{
    /// <summary>What the graph covers when no job is running.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromHours(1);

    /// <summary>
    /// The shortest window, whatever the job says.
    /// </summary>
    /// <remarks>
    /// A print thirty seconds old would otherwise draw a graph of thirty seconds. The minutes before
    /// it are not padding: they hold the heat-up, which is the one part of a print's temperature
    /// trace that has any shape to it.
    /// </remarks>
    public static readonly TimeSpan Minimum = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The longest window, whatever the job says.
    /// </summary>
    /// <remarks>
    /// Retention keeps samples for a fortnight, so a multi-day print really can ask for a hundred
    /// hours of rows. This caps what one page refresh may scan; past it the graph shows the most
    /// recent day of the job, which is where anything worth seeing happened.
    /// </remarks>
    public static readonly TimeSpan Maximum = TimeSpan.FromHours(24);

    /// <summary>
    /// The window ending now, given what the printer last reported.
    /// </summary>
    /// <param name="liveState">The printer's last-known state, or null if it has never reported.</param>
    /// <param name="now">The present, from the caller's <see cref="TimeProvider"/>.</param>
    public static (DateTimeOffset from, DateTimeOffset to) For(PrinterLiveState? liveState, DateTimeOffset now)
    {
        TimeSpan span = liveState?.TimePrinting is { } elapsed && elapsed > 0 ?
            TimeSpan.FromSeconds(elapsed) :
            Idle;

        if (span < Minimum)
        {
            span = Minimum;
        }
        else if (span > Maximum)
        {
            span = Maximum;
        }

        return (now - span, now);
    }
}
