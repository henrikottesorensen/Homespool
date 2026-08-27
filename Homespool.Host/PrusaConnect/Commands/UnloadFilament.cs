using System;
using System.Globalization;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Unloads whatever filament the printer says is loaded - <c>M702 W0</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>No arguments, and that is the whole design.</b> The temperature to unload at stays on the
/// printer: firmware reads its own stored filament type and heats to that filament's target, so
/// unloading from here and unloading at the panel cannot disagree - the same property
/// <see cref="Homespool.Host.PrusaConnect.FilamentPreset"/> buys for preheating, except that here
/// nothing has to be mirrored at all.
/// </para>
/// <para>
/// <b><c>W0</c> is what makes it headless, and it is the one argument that matters.</b> Firmware's
/// <c>W</c> selects which optional menu items the preheat dialog offers - <c>1</c> a cool-down
/// option, <c>2</c> a return option, <c>3</c> both. <c>W0</c> is <i>preheat, no return and no cool
/// down</i>: no menu entries at all. <c>255</c>, the default when <c>W</c> is omitted, means do not
/// preheat, which would try to unload through a cold nozzle.
/// </para>
/// <para>
/// <b><c>I</c> is deliberately absent.</b> It means <i>ask successful unload</i>, and it puts a
/// confirmation prompt on the panel that nobody is standing at
/// (<c>src/marlin_stubs/pause/M701_2_parse.cpp</c>, read at the pinned ref).
/// </para>
/// <para>
/// <b>This is not the <c>M1700</c> trap.</b> The panel's preheat menu cannot be driven from
/// off-machine: its arguments only choose which menu entries to
/// show, so there is no headless form of it. <c>M702</c> genuinely has one - but only while the
/// printer knows what is loaded. <c>evaluate_preheat_conditions</c>
/// (<c>M70X_preheat.cpp:201-227</c>) reads <c>config_store().get_filament_type(i)</c> for
/// <c>PreheatMode::unload</c>, and falls back to <c>preheatTempUnKnown</c> - an FSM dialog that
/// blocks until somebody answers it at the machine - when that is <c>FilamentType::none</c>.
/// <b>So the caller must establish that the printer knows its filament before sending this</b>; see
/// <see cref="Homespool.Host.PrusaConnect.PrinterFilamentService"/>, which will not send it
/// otherwise.
/// </para>
/// <para>
/// <b>It cleans up after itself.</b> <c>M702_unload</c> ends by processing a
/// <c>PreheatStatus::Result::CooledDown</c> response unconditionally
/// (<c>M701_2.cpp:179</c>, <c>:212-217</c>), which sets nozzle, bed and fan to zero. The bed is never
/// heated on the way in either - <c>PreheatBehavior::for_filament_unload</c> sets
/// <c>preheat_bed = false</c>. So the printer is left cold and empty, which is the point of sending
/// it: somebody walks in to a machine ready for a new spool.
/// </para>
/// </remarks>
public class UnloadFilament : ISendableGcodeCommand
{
    /// <summary>The most tools the wire can describe - see <see cref="ForTool"/>.</summary>
    public const int MaxTools = 8;

    private UnloadFilament(int toolIndex)
    {
        ToolIndex = toolIndex;
    }

    /// <summary>
    /// The tool this unloads, <b>0-based as gcode numbers it</b> - not as the wire does.
    /// </summary>
    public int ToolIndex { get; }

    public string WireName => "GCODE";

    public string Line => "M702 T" + ToolIndex.ToString(CultureInfo.InvariantCulture) + " W0";

    /// <summary>
    /// The command for the tool a printer calls <paramref name="wireToolNumber"/>.
    /// </summary>
    /// <param name="wireToolNumber">
    /// The tool number as telemetry and <c>INFO</c> report it, <b>1-based</b>.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>This conversion is the whole reason the constructor is private.</b> Firmware keys its slot
    /// objects <c>state.iter + 1</c> while <c>state.iter</c> <em>is</em> the <c>VirtualToolIndex</c>
    /// (<c>render.cpp:235</c> and <c>:392</c>), so the wire counts from one and gcode counts from
    /// zero. Nothing downstream can catch an off-by-one here, and the symptom is the wrong spool on
    /// the floor - so it happens once, in a named method, with a test.
    /// </para>
    /// <para>
    /// <b>Bounded at <see cref="MaxTools"/>, and that bound is firmware's rather than a guess.</b>
    /// <c>Printer::Params::slot_mask</c> is a <c>uint8_t</c> carrying a
    /// <c>static_assert(8 * sizeof slot_mask >= VirtualToolIndex::count)</c> (<c>printer.hpp:130</c>),
    /// so eight is the ceiling of the representation with no headroom. Exercised at eight on the
    /// connect rig, 2026-08-24.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Outside 1..<see cref="MaxTools"/>. Thrown rather than clamped: a tool number this application
    /// did not read off the wire is a defect, and unloading a neighbouring tool instead would hide it.
    /// </exception>
    public static UnloadFilament ForTool(int wireToolNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(wireToolNumber, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(wireToolNumber, MaxTools);

        return new UnloadFilament(wireToolNumber - 1);
    }
}
