using System;
using System.Collections.Generic;
using System.Linq;

using Homespool.Host.Telemetry;

namespace Homespool.Host.DTO;

/// <summary>Temperatures over a window, a bounded number of points however long it is.</summary>
/// <remarks>
/// <b><see cref="From"/> is the start of what there is data for</b>, which is later than the start
/// asked for when the store does not reach back that far - an axis drawn from the requested start
/// would show a gap where nothing was ever kept.
/// </remarks>
public class TemperatureSeriesReadDTO
{
    public required DateTimeOffset From { get; set; }

    public required DateTimeOffset To { get; set; }

    /// <summary>Oldest first. Empty when the printer reported nothing in the window.</summary>
    public required IReadOnlyList<TemperaturePointReadDTO> Points { get; set; }

    public static TemperatureSeriesReadDTO FromSeries(TemperatureSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);

        return new()
        {
            From = series.From,
            To = series.To,
            Points = [.. series.Points.Select(TemperaturePointReadDTO.FromPoint)],
        };
    }
}
