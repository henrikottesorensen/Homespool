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
/// printing, in attention or in error, and answers only once the unload is done. The MK3's
/// <c>M702</c> retracts the filament for somebody to pull out and leaves the heaters as they are
/// (Prusa-Firmware <c>Marlin_main.cpp:7978-7997</c>, <c>unload_filament</c> in
/// <c>ultralcd.cpp:4886-4931</c>, at <c>v3.14.1</c>).
/// </para>
/// <para>
/// <b>So the heaters are left as they were found.</b> The bed is sent the target it already has, which
/// is how it is not heated for an unload - PrusaLink sets the bed to whatever arrives. The nozzle has
/// to be heated, and is put back to its previous target by <see cref="FollowedBy"/> once the unload ends:
/// a preheated printer stays preheated, a cold one is cooled down.
/// </para>
/// </remarks>
public sealed class UnloadFilamentAt : ISendableCommand
{
    /// <summary>Creates the command.</summary>
    /// <param name="nozzleTemperature">The loaded filament's printing temperature, in °C.</param>
    /// <param name="bedTarget">The bed's target before the unload, in °C - 0 when it is off.</param>
    /// <param name="nozzleTarget">The nozzle's target before the unload, in °C - 0 when it is off.</param>
    public UnloadFilamentAt(int nozzleTemperature, int bedTarget, int nozzleTarget)
    {
        NozzleTemperature = nozzleTemperature;
        BedTarget = bedTarget;
        NozzleTarget = nozzleTarget;
    }

    /// <summary>The nozzle temperature to unload at, in °C.</summary>
    public int NozzleTemperature { get; }

    /// <summary>The bed's target before the unload, which it keeps throughout.</summary>
    public int BedTarget { get; }

    /// <summary>The nozzle's target before the unload, which it goes back to afterwards.</summary>
    public int NozzleTarget { get; }

    public string WireName => "UNLOAD_FILAMENT";

    /// <summary>Both are required: PrusaLink reads each by key, and a missing one fails the command.</summary>
    public IReadOnlyDictionary<string, object?> Arguments => new Dictionary<string, object?>
    {
        ["nozzle_temperature"] = NozzleTemperature,
        ["bed_temperature"] = BedTarget,
    };

    /// <summary>
    /// An unload runs for minutes, so its caller is answered when the printer takes it, and says the
    /// unload has started. The command holds the printer's slot until the unload ends.
    /// </summary>
    public bool AcceptedIsAnswer => true;

    /// <summary>The nozzle back to the target it had before the unload heated it.</summary>
    public ISendableCommand FollowedBy => new SetNozzleTemperature(NozzleTarget);
}
