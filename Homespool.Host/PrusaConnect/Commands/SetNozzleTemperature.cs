using System.Globalization;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Sets the nozzle's target temperature. <c>M104 S&lt;celsius&gt;</c>; zero turns the heater off.
/// </summary>
/// <remarks>
/// <b>Individually settable on purpose, and never individually exposed.</b> The nozzle and the bed
/// are separate commands because that is what the printer takes, and because a caller in the code
/// may legitimately want one without the other. The web UI offers only the paired preheat and
/// cooldown, so nothing on a page can leave a printer with one heater on and the other off by
/// accident.
/// </remarks>
public class SetNozzleTemperature : ISendableGcodeCommand
{
    public SetNozzleTemperature(int temperature)
    {
        Temperature = temperature;
    }

    /// <summary>Target in <b>degrees Celsius</b> - the unit the name no longer carries. Zero is off.</summary>
    public int Temperature { get; }

    public string WireName => "GCODE";

    public string Line => "M104 S" + Temperature.ToString(CultureInfo.InvariantCulture);
}
