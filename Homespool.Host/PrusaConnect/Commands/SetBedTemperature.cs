using System.Globalization;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Sets the heatbed's target temperature. <c>M140 S&lt;celsius&gt;</c>; zero turns the heater off.
/// </summary>
/// <remarks>
/// <c>M140</c> rather than <c>M190</c>: setting a target returns immediately, where <c>M190</c>
/// blocks the printer's gcode queue until the bed reaches it. A blocking wait would hold the
/// command channel and tell us nothing telemetry does not already report.
/// </remarks>
public class SetBedTemperature : ISendableGcodeCommand
{
    public SetBedTemperature(int temperature)
    {
        Temperature = temperature;
    }

    /// <summary>Target in <b>degrees Celsius</b> - the unit the name no longer carries. Zero is off.</summary>
    public int Temperature { get; }

    public string WireName => "GCODE";

    public string Line => "M140 S" + Temperature.ToString(CultureInfo.InvariantCulture);
}
