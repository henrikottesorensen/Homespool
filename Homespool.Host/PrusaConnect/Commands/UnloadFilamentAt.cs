using System.Collections.Generic;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Unloads filament at a nozzle temperature this application chooses - <c>UNLOAD_FILAMENT</c>, the
/// Python SDK's own command (const.py:151), for a printer that cannot say what it has loaded.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only the SDK has it.</b> Firmware has no such command and would refuse it as unknown; a Buddy
/// printer unloads with <see cref="UnloadFilament"/>, at the temperature of the filament it knows it
/// has. PrusaLink, which drives an MK3S behind the SDK, never reports what is loaded, so the person
/// unloading has to say, and the temperature comes from that filament's preset.
/// </para>
/// <para>
/// <b>What PrusaLink does with it</b> (<c>UnloadFilament</c>, command_handlers.py:400-474): sets the
/// bed to <c>bed_temperature</c>, waits with <c>M109</c> only while the nozzle is below 90% of
/// <c>nozzle_temperature</c>, sets the nozzle to it, then runs <c>M702</c>. It refuses while
/// printing, in attention or in error, and answers only once the unload is done.
/// </para>
/// <para>
/// <b>The bed is sent 0.</b> Unloading has no use for a hot bed, and PrusaLink sets the bed to
/// whatever arrives, so 0 is how it is not heated - and a bed already warm is switched off, which is
/// also how firmware's own unload leaves it.
/// </para>
/// </remarks>
public sealed class UnloadFilamentAt : ISendableCommand
{
    /// <summary>Creates the command.</summary>
    /// <param name="nozzleTemperature">The loaded filament's printing temperature, in °C.</param>
    public UnloadFilamentAt(int nozzleTemperature)
    {
        NozzleTemperature = nozzleTemperature;
    }

    /// <summary>The nozzle temperature to unload at, in °C.</summary>
    public int NozzleTemperature { get; }

    public string WireName => "UNLOAD_FILAMENT";

    /// <summary>Both are required: PrusaLink reads each by key, and a missing one fails the command.</summary>
    public IReadOnlyDictionary<string, object?> Arguments => new Dictionary<string, object?>
    {
        ["nozzle_temperature"] = NozzleTemperature,
        ["bed_temperature"] = 0,
    };

    /// <summary>
    /// An unload runs for minutes, so its caller is answered when the printer takes it, and says the
    /// unload has started. The command holds the printer's slot until the unload ends.
    /// </summary>
    public bool AcceptedIsAnswer => true;
}
