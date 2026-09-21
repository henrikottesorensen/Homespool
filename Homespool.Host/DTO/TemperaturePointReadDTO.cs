using System;

using Homespool.Host.Telemetry;

namespace Homespool.Host.DTO;

/// <summary>One bucket of the series, °C.</summary>
/// <remarks>
/// <para>
/// <b>A measurement is the bucket's average and a setpoint is its maximum</b>, because a setpoint is a
/// step: averaging across the moment it changed would report a target the printer was never given.
/// </para>
/// <para>
/// <b>Narrowed to <c>float</c> on the way out.</b> The aggregate is computed over stored floats
/// widened to doubles, and a double would print the widening - <c>214.8000030517578</c> for a reading
/// of 214.8.
/// </para>
/// </remarks>
public class TemperaturePointReadDTO
{
    /// <summary>The start of the bucket.</summary>
    public required DateTimeOffset At { get; set; }

    public float? Nozzle { get; set; }

    public float? TargetNozzle { get; set; }

    public float? Bed { get; set; }

    public float? TargetBed { get; set; }

    public float? Chamber { get; set; }

    public float? TargetChamber { get; set; }

    public float? Enclosure { get; set; }

    public static TemperaturePointReadDTO FromPoint(TemperaturePoint point)
    {
        ArgumentNullException.ThrowIfNull(point);

        return new()
        {
            At = point.At,
            Nozzle = (float?)point.Nozzle,
            TargetNozzle = (float?)point.TargetNozzle,
            Bed = (float?)point.Bed,
            TargetBed = (float?)point.TargetBed,
            Chamber = (float?)point.Chamber,
            TargetChamber = (float?)point.TargetChamber,
            Enclosure = (float?)point.Enclosure,
        };
    }
}
