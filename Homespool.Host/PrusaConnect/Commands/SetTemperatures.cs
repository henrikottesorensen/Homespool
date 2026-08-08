using System.Globalization;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Sets both heater targets in one command - <c>M140</c> then <c>M104</c>, newline separated.
/// </summary>
/// <remarks>
/// <para>
/// <b>One command rather than two, because two collide.</b> Firmware answers a gcode command
/// <c>Accepted</c> when it is <em>queued</em> as a background command and <c>Finished</c> when it
/// has run; a second command sent on the first's <c>Accepted</c> arrives while it is still executing
/// and is refused with <i>"Processing other command"</i>. Setting both heaters in one body sidesteps
/// the race entirely rather than sequencing around it, and makes the pair atomic: there is no moment
/// at which one heater is set and the other is not.
/// </para>
/// <para>
/// Firmware executes a body line by line - find the newline, run, advance, repeat
/// (<c>src/connect/background.cpp:21-87</c>) - so this is one command id with one answer, not two
/// smuggled into one frame. <see cref="GcodeAllowList"/> checks every line.
/// </para>
/// <para>
/// <b>Bed first.</b> It is the slower of the two to reach temperature, so ordering it first is free.
/// </para>
/// </remarks>
public class SetTemperatures : ISendableGcodeCommand
{
    public SetTemperatures(int nozzleTemperature, int bedTemperature)
    {
        NozzleTemperature = nozzleTemperature;
        BedTemperature = bedTemperature;
    }

    /// <summary>Nozzle target in <b>degrees Celsius</b>. Zero is off.</summary>
    public int NozzleTemperature { get; }

    /// <summary>Bed target in <b>degrees Celsius</b>. Zero is off.</summary>
    public int BedTemperature { get; }

    public string WireName => "GCODE";

    public string Line =>
        "M140 S" + BedTemperature.ToString(CultureInfo.InvariantCulture)
        + "\n"
        + "M104 S" + NozzleTemperature.ToString(CultureInfo.InvariantCulture);
}
