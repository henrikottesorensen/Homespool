using System;
using System.Globalization;

using Homespool.Host.Printing;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Unloads filament on a printer that cannot say what it has loaded - the Python SDK's, an MK3S
/// behind PrusaLink - at the temperature of the filament somebody names, and leaves the nozzle at the
/// target it had: <c>M104 S&lt;t&gt;</c>, <c>M109 S&lt;t&gt;</c>, <c>M702</c>, <c>M104 S&lt;before&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One gcode body, so the whole unload is the printer's to finish.</b> PrusaLink runs a body line by
/// line and answers once the last has been confirmed (<c>ExecuteGcode</c>, command_handlers.py:331-397),
/// so the nozzle is put back even if this application restarts or the connection drops meanwhile. It
/// refuses the whole body while printing, in attention or in error, before any line runs. Otherwise all
/// four go onto its serial queue together (<c>enqueue_list_from_str</c>, serial/helpers.py:84-109) and
/// each is only checked afterwards, so an error on an earlier line does not keep the last one from
/// reaching the printer.
/// </para>
/// <para>
/// <b>Why each line.</b> The MK3's <c>M702</c> takes only <c>U</c> and <c>Z</c>, retracts the filament
/// for somebody to pull out, and never touches a heater (Prusa-Firmware <c>Marlin_main.cpp:7978-7997</c>,
/// <c>unload_filament</c> in <c>ultralcd.cpp:4886-4931</c>, at <c>v3.14.1</c>) - so the nozzle is heated
/// first, and <c>M109</c> waits for it; on the MK3 that wait runs in either direction
/// (<c>wait_for_heater</c>, <c>Marlin_main.cpp:9277-9290</c>), so a hotter nozzle cools to <c>t</c>
/// first. The bed is not on any line: an unload has no use for it, and it is left as it is.
/// </para>
/// <para>
/// <b>The last line is filled in as the command takes the printer's slot</b>, by
/// <see cref="AsSentWith"/>, from the connection's own knowledge of the heaters. Read any earlier, it
/// could be stale - a second unload straight after a first could otherwise put back the first one's
/// unload temperature.
/// </para>
/// <para>
/// <see cref="GcodeAllowList"/> admits this exact body for the SDK's dialect and nowhere else.
/// </para>
/// </remarks>
public sealed class UnloadFilamentAt : ISendableGcodeCommand
{
    /// <summary>Creates the command, to be completed by <see cref="AsSentWith"/>.</summary>
    /// <param name="nozzleTemperature">The named filament's printing temperature, in °C.</param>
    public UnloadFilamentAt(int nozzleTemperature)
        : this(nozzleTemperature, nozzleAfterwards: null)
    {
    }

    private UnloadFilamentAt(int nozzleTemperature, int? nozzleAfterwards)
    {
        NozzleTemperature = nozzleTemperature;
        NozzleAfterwards = nozzleAfterwards;
    }

    /// <summary>The nozzle temperature to unload at, in °C.</summary>
    public int NozzleTemperature { get; }

    /// <summary>
    /// The nozzle's target before the unload, which it is set back to - null until
    /// <see cref="AsSentWith"/> has filled it in.
    /// </summary>
    public int? NozzleAfterwards { get; }

    public string WireName => "GCODE";

    /// <summary>The four lines, once <see cref="AsSentWith"/> has filled in the last.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="AsSentWith"/> filled it in.</exception>
    public string Line
    {
        get
        {
            if (NozzleAfterwards is not int after)
            {
                throw new InvalidOperationException("The unload's last line is filled in as it takes the printer's slot.");
            }

            string temperature = NozzleTemperature.ToString(CultureInfo.InvariantCulture);

            return "M104 S" + temperature + "\n" +
                   "M109 S" + temperature + "\n" +
                   "M702\n" +
                   "M104 S" + after.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// An unload runs for minutes, so its caller is answered when the printer takes it, and says the
    /// unload has started. The command holds the printer's slot until the unload ends.
    /// </summary>
    public bool AcceptedIsAnswer => true;

    /// <summary>The nozzle back at the target it had; the bed untouched.</summary>
    public HeaterChange? LeavesHeaters => NozzleAfterwards is int after ? new HeaterChange(after, Bed: null) : null;

    /// <summary>Fills in the last line with the nozzle's target as the connection knows it.</summary>
    public ISendableCommand AsSentWith(HeaterTargets heaters)
    {
        ArgumentNullException.ThrowIfNull(heaters);

        return new UnloadFilamentAt(NozzleTemperature, heaters.Nozzle);
    }
}
