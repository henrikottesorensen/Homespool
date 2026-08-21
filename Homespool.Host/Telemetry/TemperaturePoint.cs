using System;

namespace Homespool.Host.Telemetry;

/// <summary>
/// One bucket of the temperature graph - what the printer's heaters were doing over a slice of the
/// window.
/// </summary>
/// <param name="At">The bucket's start, which is where the point is drawn.</param>
/// <param name="Nozzle">Mean nozzle temperature over the bucket, or null if it reported none.</param>
/// <param name="Bed">Mean bed temperature over the bucket, or null if it reported none.</param>
/// <param name="TargetNozzle">The highest nozzle setpoint asked for during the bucket.</param>
/// <param name="TargetBed">The highest bed setpoint asked for during the bucket.</param>
/// <remarks>
/// <b>The two pairs are summarised differently on purpose.</b> A measured temperature is a
/// continuous quantity and a mean over the bucket is what the line between two points already
/// claims. A setpoint is a step: averaging across the moment it changes invents a target the printer
/// was never given, so the higher of the two is taken instead - which errs towards showing that
/// something was asked to be hot, rather than smoothing the request away.
/// </remarks>
public sealed record TemperaturePoint(DateTimeOffset At,
                                      double? Nozzle,
                                      double? Bed,
                                      double? TargetNozzle,
                                      double? TargetBed);
