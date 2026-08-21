namespace Homespool.Host.Pages.Printers;

/// <summary>
/// One heater's line on the status card: where it is, where it is going, and which of those.
/// </summary>
public sealed record HeaterReading(float? Current, float? Target, HeaterState State)
{
    /// <summary>
    /// How close counts as "at target", in degrees.
    /// </summary>
    /// <remarks>
    /// A real MK3.5 holding 215 reports 214.6 and 215.3 either side of a second. Comparing floats for
    /// equality would leave a card flickering between "heating" and "at target" once a second and
    /// saying nothing true - and against SQLite's widened doubles it would essentially never match at
    /// all (<c>notes/floating-point.md</c>).
    /// </remarks>
    public const float Tolerance = 2;

    /// <summary>
    /// Above this, a heater with no setpoint is called cooling rather than off.
    /// </summary>
    /// <remarks>
    /// Warm enough to burn and warm enough to matter when deciding whether to reach into the
    /// machine, and comfortably above any room this will run in.
    /// </remarks>
    public const float WarmAbove = 40;

    /// <summary>Reads a heater from the pair of numbers telemetry carries.</summary>
    /// <remarks>
    /// <b>A target of zero means off, not "asked for zero degrees".</b> Firmware reports the setpoint
    /// as zero when a heater is switched off, which is also what a cooldown sets - so the two are the
    /// same state and are treated as one.
    /// </remarks>
    public static HeaterReading For(float? current, float? target)
    {
        if (current is not { } now)
        {
            return new HeaterReading(current, target, HeaterState.Unknown);
        }

        if (target is not { } wanted || wanted <= 0)
        {
            return new HeaterReading(current, target, now > WarmAbove ? HeaterState.Cooling : HeaterState.Off);
        }

        if (now >= wanted - Tolerance && now <= wanted + Tolerance)
        {
            return new HeaterReading(current, target, HeaterState.AtTarget);
        }

        return new HeaterReading(current, target, now < wanted ? HeaterState.Heating : HeaterState.Cooling);
    }
}
