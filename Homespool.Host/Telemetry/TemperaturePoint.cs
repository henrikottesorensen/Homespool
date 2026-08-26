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
/// <param name="Chamber">Mean chamber temperature, on a printer with a managed chamber.</param>
/// <param name="TargetChamber">The highest chamber setpoint asked for during the bucket.</param>
/// <param name="Enclosure">Mean enclosure temperature, on a printer with an enclosure fitted.</param>
/// <remarks>
/// <para>
/// <b>The measurements and the setpoints are summarised differently on purpose.</b> A measured
/// temperature is a continuous quantity and a mean over the bucket is what the line between two
/// points already claims. A setpoint is a step: averaging across the moment it changes invents a
/// target the printer was never given, so the higher of the two is taken instead - which errs
/// towards showing that something was asked to be hot, rather than smoothing the request away.
/// </para>
/// <para>
/// <b><paramref name="Chamber"/> and <paramref name="Enclosure"/> are two sensors, not one under two
/// names.</b> The chamber block carries a setpoint; <c>enclosure</c> arrives as
/// <c>JSON_FIELD_INT</c> with none beside it, which is a gap in what is reported rather than a claim
/// about what heats it. Neither says which element does the work, and nothing here needs to know. No
/// printer seen so far reports both, but nothing in the schema or on the wire forbids it, so they
/// stay separate rather than merging behind one label that would silently drop whichever lost.
/// </para>
/// <para>
/// All three are null on every single-chamber printer, which is most of them. That is what keeps the
/// traces off the graph rather than drawing them along the bottom - see
/// <c>TemperatureChart.PathFor</c>, where a null lifts the pen.
/// </para>
/// </remarks>
public sealed record TemperaturePoint(DateTimeOffset At,
                                      double? Nozzle,
                                      double? Bed,
                                      double? TargetNozzle,
                                      double? TargetBed,
                                      double? Chamber = null,
                                      double? TargetChamber = null,
                                      double? Enclosure = null);
