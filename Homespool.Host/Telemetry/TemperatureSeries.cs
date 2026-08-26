using System;
using System.Collections.Generic;

namespace Homespool.Host.Telemetry;

/// <summary>
/// The temperature graph's data: a window, and the buckets covering it.
/// </summary>
/// <remarks>
/// <see cref="Points"/> is empty for a printer that reported nothing in the window - a machine
/// switched off, or one that has never connected. That is a legitimate answer rather than a failure,
/// and the page says so instead of drawing empty axes.
/// </remarks>
public sealed record TemperatureSeries(DateTimeOffset From,
                                       DateTimeOffset To,
                                       IReadOnlyList<TemperaturePoint> Points)
{
    /// <summary>An empty series over the given window.</summary>
    public static TemperatureSeries Empty(DateTimeOffset from, DateTimeOffset to)
    {
        return new TemperatureSeries(from, to, []);
    }
}
